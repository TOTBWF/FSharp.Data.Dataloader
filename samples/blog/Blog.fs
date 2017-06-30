module Tests
open System
open FSharp.Data.Dataloader


// === An API ===

type PostId = int
type PostContent = string
type Enviroment = {
    PostIds : PostId list
    PostContents : Map<PostId, string>
}

let env = {
    PostIds = [1;2]
    PostContents = Map.ofList [1, "Test1"; 2, "Test2"]
}

// The original HAXL library relies HEAVYILY on GADTS for request types
// This presents some problems for F#, as we don't have that great of a way of 
// expressing that.

type BlogRequest =
    | FetchPosts
    | FetchPostContent of PostId
    interface Request with
        member x.Identifier =
            match x with
            | FetchPosts -> "FetchPosts"
            | FetchPostContent id -> "FetchContent id = " + id.ToString()
        

let blogDataSource = 
    let fetchfn (blocked: BlockedFetch<BlogRequest> list) = 
        blocked
        |> List.iter(fun b ->
            match b.Request with
            | FetchPosts -> 
                let ids = env.PostIds
                FetchResult.putSuccess (b.Status) (box ids)
            | FetchPostContent id ->
                let content = Map.tryFind id env.PostContents
                FetchResult.putSuccess (b.Status) (box content))
        |> SyncFetch
    DataSource.create "Blog" fetchfn 


[<EntryPoint>]
let main args = 
    printfn "Starting Fetch!"
    let fetchPostIds = Fetch.dataFetch<PostId list, BlogRequest> blogDataSource (FetchPosts)
    let fetchPostContent id = Fetch.dataFetch<string option, BlogRequest> blogDataSource (FetchPostContent id)
    let renderPosts postIds posts = 
        List.zip postIds posts
        |> List.map(fun (c, id) -> sprintf "Id: %d\nPost: %s" c id) |> List.fold(fun acc e -> e + "\n" + acc) ""
    let posts = 
        fetchPostIds
        |> Fetch.bind(Fetch.mapSeq fetchPostContent)
        |> Fetch.map(Seq.map(Option.defaultWith(fun () -> "")) >> Seq.toList)
    let contents =
        fetchPostIds
        |> Fetch.map(renderPosts)
        |> Fetch.applyTo posts
    let res =  Fetch.runFetch true contents
    printfn "Results:\n%s" res
    0