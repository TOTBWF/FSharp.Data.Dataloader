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

type BlogRequest<'a> =
    | FetchPosts of (PostId list -> 'a)
    | FetchPostContent of PostId * (PostContent option -> 'a)
    interface Request<'a> with
        member x.Identifier =
            match x with
            | FetchPosts _ -> "FetchPosts"
            | FetchPostContent(id, _) -> "FetchContent id = " + id.ToString()
        

let blogDataSource() = 
    let fetchfn (blocked: BlockedFetch<'a, BlogRequest<'a>> list) = 
        blocked
        |> List.iter(fun b ->
            match b.Request with
            | FetchPosts cont -> 
                let ids = env.PostIds
                FetchResult.putSuccess (b.Status) (cont ids)
            | FetchPostContent(id, cont) ->
                let content = Map.tryFind id env.PostContents
                FetchResult.putSuccess (b.Status) (cont content))
        |> SyncFetch
    DataSource.create "Blog" fetchfn 


[<EntryPoint>]
let main args = 
    printfn "Starting Fetch!"
    let fetchPostIds = Fetch.dataFetch<PostId list, BlogRequest<_>> (blogDataSource()) (FetchPosts id)
    let fetchPostContent postId = Fetch.dataFetch<PostContent option, BlogRequest<_>> (blogDataSource()) (FetchPostContent(postId, id))
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