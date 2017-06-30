# FSharp.Data.Dataloader


### What is Dataloader?
Dataloader is a generic utility meant to be used in your application's data fetch layer to
provide batching and caching.

This is a spiritual port of the [Haxl](https://github.com/facebook/Haxl) library for Haskell.


## Getting Started
First, install and build the library.

```sh
cd src
dotnet restore
dotnet build
```
For our example, we are going to show how to make a simple blog using Dataloader.


First we are going to make an instance of a request:
```fsharp
type BlogRequest<'a> =
    | FetchPosts of (int list -> 'a)
    | FetchPostContent of int * (string option -> 'a)
    interface Request<'a> with
        member x.Identifier =
            match x with
            | FetchPosts -> "FetchPosts"
            | FetchPostContent id -> "FetchContent id = " + id.ToString()
```

A request encapsulates all of the possible requests you could make to a given datasource, as well as the data needed to make those requests.

Next up we are going to create our DataSource
```fsharp
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
```

The most important part of our datasource is the `fetchfn` function, which describes how we are going to resolve a batch of requests that we are currently blocking on. Note that it returns a `PerformFetch`, which is defined as such:
```fsharp
type PerformFetch =
    | SyncFetch of unit
    | AsyncFetch of Async<unit []>
```
This allows us to do fully asynchronous requests, but for now we are just going to use the synchronous version.

Finally, let us see it all composed together:
```fsharp
let fetchPostIds = Fetch.dataFetch<int list, BlogRequest<_>> (blogDataSource()) (FetchPosts id)
let fetchPostContent postId = Fetch.dataFetch<string option, BlogRequest<_>> source (FetchPostContent(postId, id))
let renderPosts posts postIds = 
    List.zip posts postIds 
    |> List.map(fun (c, id) -> sprintf "Id: %d\n Post: %s" c id) |> List.fold(fun acc e -> e + "\n" + acc) ""
let posts = 
    fetchPostIds
    |> Fetch.bind(Fetch.mapSeq fetchPostContent)
    |> Fetch.map(Seq.map(Option.defaultWith(fun () -> "")) >> Seq.toList)
let contents =
    fetchPostIds
    |> Fetch.map(renderPosts)
    |> Fetch.applyTo posts
let res =  Fetch.runFetch contents
 ```

Cool! Even though we call `fetchPostIds` twice, it will only make that fetch once! This example doesnt gain that much from request batching, but if we decided to implement it (say, if we had a SQL datasource), we would implement it within the `FetchFn` of our DataSource.