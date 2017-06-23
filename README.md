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
type BlogRequest =
    | FetchPosts
    | FetchPostContent of int
    interface Request with
        member x.Identifier =
            match x with
            | FetchPosts -> "FetchPosts"
            | FetchPostContent id -> "FetchContent id = " + id.ToString()
```

A request encapsulates all of the possible requests you could make to a given datasource, as well as the data needed to make those requests.

Next up we are going to create our DataSource
```fsharp
type BlogDataSource() = 
    interface DataSource<BlogRequest> with
        member x.Name = "Blog"
        member x.FetchFn (blocked: BlockedFetch<BlogRequest> list): PerformFetch = 
            // This is where you could do batching, concurrency, asynchronicity, etc
            blocked
            |> List.iter(fun b ->
                match b.Request with
                | FetchPosts -> 
                    let ids = env.PostIds
                    b.Status := FetchSuccess(box ids) // <- Set the status ref to the result of the fetch
                | FetchPostContent id ->
                    let content = Map.tryFind id env.PostContents // <- See the sample for a full description of this
                    b.Status := FetchSuccess(box content)) // <- Set the status ref to the result of the fetch
            |> SyncFetch
```
The most important part of our datasource is the `FetchFn` function, which describes how we are going to resolve a batch of requests that we are currently blocking on. Note that it returns a `PerformFetch`, which is defined as such:
```fsharp
type PerformFetch =
    | SyncFetch of unit
    | AsyncFetch of Async<unit>
```
This allows us to do fully asynchronous requests, but for now we are just going to use the synchronous version.

Finally, let us see it all composed together:
```fsharp
let source = BlogDataSource()
let fetchPostIds = Fetch.dataFetch<PostId list, BlogRequest> source (FetchPosts)
let fetchPostContent id = Fetch.dataFetch<string option, BlogRequest> source (FetchPostContent id)
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