module FSharp.Data.Dataloader

open System
open System.Reflection
open FSharp.Reflection.FSharpReflectionExtensions
open System.Collections.Concurrent

/// Represents an instruction to the system on how to fetch a value of type 'a
type Fetch<'a> = 
    { unFetch : Environment -> Result<'a> }


and Environment = {
    Cache : DataCache ref
    Store : RequestStore ref
    Trace : bool
}

/// Represents some Monadic operation on a fetch value
and FetchExpr<'a> = 
    abstract ToFetch : unit -> Fetch<'a>
    abstract MapCompose : ('a -> 'c) -> FetchExpr<'c> option 
    abstract BindCompose : ('a -> Fetch<'c>) -> FetchExpr<'c> option 
    
/// Type representing the result of a data fetch
/// Done represents a completed fetch with value of type 'a
and Result<'a> =
    | Done of 'a
    | Blocked of BlockedFetch list * FetchExpr<'a>
    | FailedWith of exn

/// Type representing the status of a blocked request
and FetchStatus<'a> =
    | NotFetched
    | FetchSuccess of 'a
    | FetchError of exn

/// Untyped version of our FetchResult reference wrapper, used for caching
and FetchResult = 
    abstract GetStatus : unit -> FetchStatus<obj>
    abstract SetStatus : FetchStatus<obj> -> unit

/// Typed version of our FetchResult reference wrapper, to be presented to the user
and FetchResult<'a> = 
    inherit FetchResult
    abstract GetStatus : unit -> FetchStatus<'a>
    abstract SetStatus : FetchStatus<'a> -> unit

/// Creates a wrapper around a mutable status value, with typed and untyped accessors
and internal FetchResultDefinition<'a>(status: FetchStatus<'a>) = 
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
and PerformFetch =
    | SyncFetch of (unit -> unit)
    | AsyncFetch of (unit -> Async<unit>)

/// Represents an untyped Request, used to power the caching side
and Request = 
    abstract Identifier : string

/// Represents an typed Request, used to implement the user defined request types
and Request<'a> =  
    inherit Request

/// Untyped version of Datasource, used primarily for heterogenous caches
and DataSource = 
    abstract Name : string
    abstract FetchFn : BlockedFetch list -> PerformFetch list

/// A source of data that will be fetched from
and DataSource<'a, 'r when 'r :> Request<'a>> =
    /// Applied for every blocked request in a given round
    abstract FetchFn : BlockedFetch<'a, 'r> list -> PerformFetch list
    inherit DataSource

/// An implementation of the data source interfaces
/// Used so that we can transform between typed and untyped versions
and internal DataSourceDefinition<'a, 'r when 'r :> Request<'a>> = 
    {
        Name : string
        FetchFn : BlockedFetch<'a, 'r> list -> PerformFetch list
    }
    interface DataSource with
        member x.Name = x.Name
        // Cast the list of blocked fetches to the typed variant, then feed them into the fetchFn
        member x.FetchFn blocked = blocked |> List.map(fun b -> b :?> BlockedFetch<'a, 'r>) |> x.FetchFn
    interface DataSource<'a, 'r> with
        member x.FetchFn blocked = x.FetchFn blocked
    

/// Untyped verion of a blocked fetch, used for the internals of the library
and BlockedFetch =
    abstract Request : Request
    abstract Status : FetchResult

/// Typed version of a blocked fetch, presented to the user as a part of FetchFn
and BlockedFetch<'a, 'r when 'r :> Request<'a>> =
    abstract Request : 'r
    abstract Status : FetchResult<'a>
    inherit BlockedFetch

/// An implementation of the blockedFetch interfaces
/// Used so that we can freely move between the typed and untyped versions 
and internal BlockedFetchDefinition<'a, 'r when 'r :> Request<'a>> = 
    {
        Request : 'r
        Status : FetchResult<'a>
    }
    interface BlockedFetch<'a, 'r> with
        member x.Request = x.Request
        member x.Status = x.Status
    interface BlockedFetch with
        member x.Request = upcast x.Request
        member x.Status = upcast x.Status

/// Our cache of result values
and DataCache = ConcurrentDictionary<string, FetchResult>

/// When a request is issued by the client via a 'dataFetch',
/// It is placed in the RequestStore. When we are ready to fetch a batch of requests,
/// 'performFetch' is called
and RequestStore = ConcurrentDictionary<string, DataSource * BlockedFetch list>

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
    
    let resolve (fn: DataSource -> BlockedFetch list -> PerformFetch list) (store: RequestStore) =
        let collect =
            List.fold(fun acc e ->
                match e with
                | SyncFetch cont -> 
                    cont()
                    acc
                | AsyncFetch cont ->
                    let a = cont()
                    a::acc) []
        let asyncs = store |> Seq.fold(fun acc (KeyValue(_, (s, b))) -> (collect (fn s b))@acc) []
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
    let create (name: string) (fetchfn: BlockedFetch<'a, 'r> list -> PerformFetch list): DataSource<'a, 'r> = upcast { DataSourceDefinition.Name = name; FetchFn = fetchfn }  

[<RequireQualifiedAccess>]
module Fetch =


    /// Represents a monadic operation on a fetch value
    type Expr<'b, 'a> =
        | ConstExpr of Fetch<'a>
        | MapExpr of ('b -> 'a) * FetchExpr<'b>
        | ApplyExpr of FetchExpr<'b -> 'a> * FetchExpr<'b>
        | BindExpr of ('b -> Fetch<'a>) * FetchExpr<'b>

        // === KLUDGE ALERT ===
        // There is some issues with using recursive modules and Generics (The generic can somehow leak...)
        // Because of this, we need to define all of our monadic functions twice, so once for the expr to use
        // The other for the Fetch module. If we dont, we cant order them in such a way that they can see one another
        static member Map f a =
                let unFetch = fun env ->
                    match a.unFetch env with
                    | Done x -> Done(f x)
                    | Blocked (br, c) -> Blocked(br, MapExpr(f, c))
                    | FailedWith exn -> FailedWith exn
                { unFetch = unFetch }
        static member ApplyTo a f =
            let unFetch = fun env ->
                match a.unFetch env, f.unFetch env with
                | Done a', Done f' -> Done(f' a')
                | Done a', Blocked(br, f') -> Blocked(br, MapExpr((|>) (a'), f'))
                | Blocked(br, a'), Done(f') -> Blocked(br, MapExpr(unbox >> f', a'))
                | Blocked(br1, a'), Blocked(br2, f') -> Blocked(br1@br2, ApplyExpr(f', a'))
                | FailedWith exn, _ -> FailedWith exn
                | _, FailedWith exn -> FailedWith exn
            { unFetch = unFetch }
        
        /// Applies some binding function f to the inner value of a
        static member Bind f a =
            let unFetch = fun env ->
                match a.unFetch env with
                | Done x -> (f x).unFetch env
                | Blocked(br, c) -> Blocked(br, BindExpr(f, c))
                | FailedWith exn -> FailedWith exn
            { unFetch = unFetch }

        interface FetchExpr<'a> with
            // Used to compose two consecutive map functions into one
            member x.MapCompose (f: 'a -> 'c): FetchExpr<'c> option =
                match x with
                | MapExpr(g, v) -> MapExpr(g >> f, v) :> FetchExpr<'c> |> Some
                | _ -> None
            // Used to compose two consecutive bind functions into one
            member x.BindCompose<'c> (f: 'a -> Fetch<'c>): FetchExpr<'c> option =
                match x with
                | BindExpr(g, v) -> BindExpr((fun b -> Expr<_,_>.Bind f (g b)), v) :> FetchExpr<'c> |> Some
                | _ -> None
            // Transforms an Expr into a fetch, while applying tree optimizations
            member x.ToFetch () = 
                match x with
                | ConstExpr f -> f
                | MapExpr(f, v) ->
                    match v.MapCompose(f) with
                    | Some e -> e.ToFetch()
                    | None -> Expr<_,_>.Map f (v.ToFetch())
                | ApplyExpr(f, v) -> Expr<_,_>.ApplyTo (v.ToFetch()) (f.ToFetch())
                | BindExpr(f, v) ->
                    match v.BindCompose(f) with
                    | Some e -> e.ToFetch()
                    | None -> Expr<_,_>.Bind f (v.ToFetch())


    // Takes a cont and an op and turns it into an ExFetchOp
    /// Lifts a value into a completed fetch
    let lift a = { unFetch = fun env -> Done(a)}

    let failedwith exn = { unFetch = fun env -> FailedWith exn}
    /// Applies a mapping function f to the inner value of a
    let map = Expr<_,_>.Map

    /// Applies some wrapped function f to the inner value of a
    let applyTo = Expr<_,_>.ApplyTo
    
    /// Applies some binding function f to the inner value of a
    let rec bind = Expr<_,_>.Bind

    /// Creates a fetch that runs 2 fetches concurrently and the collects their result as a tuple
    let zip f1 f2 = 
        applyTo f2 (map (fun a b -> a, b) f1)
    
    /// Applies a bind function to a sequence of values, production a fetch of a sequence
    let mapSeq (f: 'a -> Fetch<'b>) (a: seq<'a>) =
        let cons (x: 'a) ys = (f x) |> map(fun v -> Seq.append [v]) |> applyTo ys
        Seq.foldBack cons a (lift Seq.empty)

    let iterSeq (f: 'a -> Fetch<unit>) (a: seq<'a>): Fetch<unit> =
        Seq.fold(fun _ e -> f e) (lift ()) a
    
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
                | FetchError e -> raise e
                | NotFetched -> FailedWith (Failure "Expected Complete Fetch!")
            { unFetch = unFetch } |> ConstExpr
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
    

    /// Creates a Fetch from a Request that will ignore the cache. This is useful in cases where the fetch will be performing mutations.
    /// Be careful that any mutations do not conflict with reads of the same data.
    let uncachedFetch<'a, 'r when 'r :> Request<'a>> (d: DataSource<'a, 'r>) (a: 'r): Fetch<'a> =
        let cont (statusWrapper: FetchResult<'a>) = 
            let unFetch env = 
                match statusWrapper.GetStatus() with
                | FetchSuccess s -> Done(s)
                | FetchError e -> raise e
                | _ -> FailedWith (Failure "Expected Complete Fetch!")
            { unFetch = unFetch } |> ConstExpr
        let unFetch env =
            // We are going to totally skip the cache here
            if env.Trace then printfn "Request %s is being run as an uncached request" a.Identifier
            let status = FetchResultDefinition(NotFetched)
            let blockedReq = {Request = a; Status = status}
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
        let rec helper (f: Fetch<'a>) =
            match f.unFetch env with
            | Done a -> 
                if trace then printfn "Fetch is completed!"
                a
            | Blocked(br, cont) ->
                if trace then printfn "Beginning fetch with round size %d" br.Length
                performFetches (!(env.Store))
                // Clear out the request cache
                env.Store := RequestStore.empty()
                helper (cont.ToFetch())
            | FailedWith ex -> raise ex
        helper fetch

[<AutoOpen>]
module FetchExtensions =
    type FetchBuilder() = 
        member __.Return(x) = Fetch.lift x
        member __.ReturnFrom(x) = x
        member __.Zero() = Fetch.lift ()
        member __.Bind(m, f) = Fetch.bind f m
        /// We need to overload bind for tuples to make use of batching
        member __.Bind((m1, m2), f) = Fetch.bind f (Fetch.zip m1 m2) 
        member __.Combine(m1, m2) = Fetch.bind(fun _ -> m2) m1
    let fetch = FetchBuilder()