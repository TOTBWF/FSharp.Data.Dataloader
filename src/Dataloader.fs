module rec FSharp.Data.Dataloader

open System
open System.Reflection
open System.Collections.Concurrent

/// Represents an instruction to the system on how to fetch a value of type 'a
type Fetch<'a> = { unFetch : Environment -> Result<'a> }

type Environment = {
    Cache : DataCache ref
    Store : RequestStore ref
    Trace : bool
}
/// Type representing the result of a data fetch
/// Done represents a completed fetch with value of type 'a
type Result<'a> =
    | Done of 'a
    | Blocked of BlockedFetch list * Fetch<'a>
    | FailedWith of exn

/// Type representing the status of a blocked request
type FetchStatus<'a> =
    | NotFetched
    | FetchSuccess of 'a
    | FetchError of exn

type FetchResult = 
    abstract GetStatus : unit -> FetchStatus<obj>
    abstract SetStatus : FetchStatus<obj> -> unit

type FetchResult<'a> = 
    inherit FetchResult
    abstract GetStatus : unit -> FetchStatus<'a>
    abstract SetStatus : FetchStatus<'a> -> unit

/// Creates a wrapper around a mutable status value, with typed and untyped accessors
type internal FetchResultDefinition<'a>(status: FetchStatus<'a>) = 
    let boxStatus s = 
        match s with
        | NotFetched -> NotFetched
        | FetchSuccess a -> FetchSuccess (box a)
        | FetchError exn -> FetchError exn

    let unboxStatus (s: FetchStatus<obj>) = 
        match s with
        | NotFetched -> NotFetched
        | FetchSuccess o -> FetchSuccess (unbox o)
        | FetchError exn -> FetchError exn
    let mutable (__status: FetchStatus<obj>) = boxStatus status
    interface FetchResult<'a> with
        member x.GetStatus () = unboxStatus __status
        member x.SetStatus status = __status <- (boxStatus status)

    interface FetchResult with
        member x.GetStatus () = __status
        member x.SetStatus status = __status <- status


/// Result type of a fetch
/// If the result type is asynchronous then all results will be batched
type PerformFetch =
    | SyncFetch of unit
    | AsyncFetch of Async<unit []>

/// Represents an untyped Request, used to power the caching side
type Request = 
    abstract Identifier : string

type Request<'a> =  
    inherit Request

/// Untyped version of Datasource, used primarily for heterogenous caches
type DataSource = 
    abstract Name : string
    abstract FetchFn : BlockedFetch list -> PerformFetch

/// A source of data that will be fetched from, with Request type 'r
type DataSource<'a, 'r when 'r :> Request<'a>> =
    /// Applied for every blocked request in a given round
    abstract FetchFn : BlockedFetch<'a, 'r> list -> PerformFetch
    inherit DataSource

type internal DataSourceDefinition<'a, 'r when 'r :> Request<'a>> = 
    {
        Name : string
        FetchFn : BlockedFetch<'a, 'r> list -> PerformFetch
    }
    interface DataSource with
        member x.Name = x.Name
        member x.FetchFn blocked = blocked |> List.map(fun b -> b :?> BlockedFetch<'a, 'r>) |> x.FetchFn
    interface DataSource<'a, 'r> with
        member x.FetchFn blocked = x.FetchFn blocked
    

type BlockedFetch =
    abstract Request : obj
    abstract Status : FetchResult

type BlockedFetch<'a, 'r when 'r :> Request<'a>> =
    abstract Request : 'r
    abstract Status : FetchResult<'a>
    inherit BlockedFetch

type internal BlockedFetchDefinition<'a, 'r when 'r :> Request<'a>> = 
    {
        Request : 'r
        Status : FetchResult<'a>
    }
    interface BlockedFetch<'a, 'r> with
        member x.Request = x.Request
        member x.Status = x.Status
    interface BlockedFetch with
        member x.Request = box x.Request
        member x.Status = upcast x.Status

/// When a reques
type DataCache = ConcurrentDictionary<string, FetchResult>

/// When a request is issued by the client via a 'dataFetch',
/// It is placed in the RequestStore. When we are ready to fetch a batch of requests,
/// 'performFetch' is called
type RequestStore = ConcurrentDictionary<string, DataSource * BlockedFetch list>

[<RequireQualifiedAccess>]
module internal DataCache =
    let empty (): DataCache = new ConcurrentDictionary<string, FetchResult>()
    let add (r: Request) (status: FetchResult) (c: DataCache): DataCache =
        c.AddOrUpdate(r.Identifier, status, (fun _ _ -> status)) |> ignore
        c
    
    let get<'a> (r: Request) (c: DataCache): FetchResult<'a> option =
        match c.TryGetValue(r.Identifier) with
        | true, status -> 
            Some (downcast status)
        | false, _ -> 
            None

[<RequireQualifiedAccess>]
module internal RequestStore =
    let empty (): RequestStore = new ConcurrentDictionary<string, DataSource * BlockedFetch list>()
    let addRequest (r: BlockedFetch) (source: DataSource) (store: RequestStore) =
        store.AddOrUpdate(source.Name, (source, [r]), (fun _ (_, requests) -> (source, r::requests))) |> ignore
        store
    
    let resolve (fn: DataSource -> BlockedFetch list -> PerformFetch) (store: RequestStore) =
        let asyncs = 
            store
            |> Seq.fold(fun acc (KeyValue(_, (s, b))) -> 
                match fn s b with
                | SyncFetch _ -> acc
                | AsyncFetch a -> a::acc) [] 
        asyncs |> Async.Parallel |> Async.RunSynchronously |> ignore

[<RequireQualifiedAccess>]
module FetchResult =
    let putSuccess (f: FetchResult<'a>) (v: 'a) =
        f.SetStatus(FetchSuccess(v))
    
    let putFailure (f: FetchResult<'a>) (e: exn)=
        let (fe: FetchStatus<'a>) = FetchError(e)
        f.SetStatus(fe)

[<RequireQualifiedAccess>]
module DataSource =
    let create (name: string) (fetchfn: BlockedFetch<'a, 'r> list -> PerformFetch): DataSource<'a, 'r> = upcast { DataSourceDefinition.Name = name; FetchFn = fetchfn }  
[<RequireQualifiedAccess>]
module Fetch =
    let lift a = { unFetch = fun env -> Done(a)}

    let failedwith exn = { unFetch = fun env -> FailedWith exn}
    /// Applies a mapping function f to the inner value of a
    let rec map f a =
        let unFetch = fun env ->
            match a.unFetch env with
            | Done x -> Done(f x)
            | Blocked (br, c) -> Blocked(br, (map f c))
            | FailedWith exn -> FailedWith exn
        { unFetch = unFetch }

    /// Applies some wrapped function f to the inner value of a
    let rec applyTo (a: Fetch<'a>) (f: Fetch<('a -> 'b)>) =
        let unFetch = fun env ->
            match a.unFetch env, f.unFetch env with
            | Done a', Done f' -> Done(f' a')
            | Done a', Blocked(br, f') -> Blocked(br, applyTo { unFetch = fun env -> Done a' } f')
            | Blocked(br, a'), Done(f') -> Blocked(br, map f' a')
            | Blocked(br1, a'), Blocked(br2, f') -> Blocked(br1@br2, applyTo a' f')
            | FailedWith exn, _ -> FailedWith exn
            | _, FailedWith exn -> FailedWith exn
        { unFetch = unFetch }
    
    /// Applies some binding function f to the inner value of a
    let rec bind f a =
        let unFetch = fun env ->
            match a.unFetch env with
            | Done x -> (f x).unFetch env
            | Blocked(br, x) -> Blocked(br, bind f x)
            | FailedWith exn -> FailedWith exn
        { unFetch = unFetch }
    
    /// Applies a bind function to a sequence of values, production a fetch of a sequence
    let mapSeq (f: 'a -> Fetch<'b>) (a: seq<'a>) =
        let cons (x: 'a) ys = (f x) |> map(fun v -> Seq.append [v]) |> applyTo ys
        Seq.foldBack cons a (lift Seq.empty)
    
    /// Collects a seq of fetches into a singular fetch of sequences
    let collect (a: seq<Fetch<'a>>): Fetch<seq<'a>> =
        let cons (x: Fetch<'a>) ys = x |> map(fun v -> Seq.append [v]) |> applyTo ys
        Seq.foldBack cons a (lift Seq.empty)
    
    /// Transforms a request into a fetch operation
    let dataFetch<'a, 'r when 'r :> Request<'a>> (d: DataSource<'a, 'r>) (a: 'r): Fetch<'a> =
        let cont (statusWrapper: FetchResult<'a>) = 
            let unFetch env = 
                match statusWrapper.GetStatus() with
                | FetchSuccess s -> Done(s)
                | _ -> FailedWith (Failure "Expected Complete Fetch!")
            { unFetch = unFetch }
        let unFetch env =
            let cache = !(env.Cache)
            // Do a lookup in the cache to see if we need to return 
            match DataCache.get<'a> a cache with
            | Some statusWrapper ->
                match statusWrapper.GetStatus() with
                | FetchSuccess v ->
                    if env.Trace then printfn "Request %s found in cache" a.Identifier
                    // We've seen the request before, and it is completed, so return the value
                    Done(v)
                | NotFetched ->
                    if env.Trace then printfn "Request %s found in request store" a.Identifier
                    // We've seen the request before, but it is blocked, but we dont add the request to the RequestStore
                    Blocked([], cont statusWrapper)
                | FetchError ex ->
                    if env.Trace then printfn "Request %s failed with exception %s" a.Identifier ex.Message
                    // There was an error, so add the failure as our result
                    FailedWith ex
            | None -> 
                if env.Trace then printfn "Request %s not found in either request store or cache" a.Identifier
                // We haven't seen the request before, so add it to the request store so it will be fetched in the next round
                let status = FetchResultDefinition(NotFetched)
                let blockedReq = {Request = a; Status = status}
                // Update the cache and store references
                DataCache.add a status cache |> ignore
                RequestStore.addRequest blockedReq d !(env.Store) |> ignore
                Blocked([blockedReq], cont status)
        { unFetch = unFetch }
    
    /// Issues a batch of fetches to the request store. After 
    /// 'performFetchs' is complete, all of the BlockedRequests status refs are full
    let performFetches (store: RequestStore) =
        RequestStore.resolve(fun source blocked -> source.FetchFn blocked) store

    /// Executes a fetch using fn to resolve the blocked requests
    /// Fn should fill in the reference value of the BlockedRequest
    let runFetch trace fetch =
        let env = { Cache = ref (DataCache.empty ()); Store = ref (RequestStore.empty ()) ; Trace = trace}
        let rec helper f =
            match f.unFetch env with
            | Done a -> 
                if trace then printfn "Fetch is completed!"
                a
            | Blocked(br, cont) ->
                if trace then printfn "Beginning fetch with round size %d" br.Length
                performFetches (!(env.Store))
                // Clear out the request cache
                env.Store := RequestStore.empty()
                helper cont
            | FailedWith ex -> raise ex
        helper fetch

