module Echelon.Signal.Tests.Support

open System.IO

/// The repository root: the directory holding Echelon.Signal.sln.
let repositoryRoot =
    let rec up (directory: DirectoryInfo) =
        match directory with
        | null -> failwith "Echelon.Signal.sln not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Echelon.Signal.sln")) -> d.FullName
        | d -> up d.Parent

    up (DirectoryInfo(System.AppContext.BaseDirectory))

let repoFile (relative: string) = Path.Combine(repositoryRoot, relative)

let readRepoFile (relative: string) = File.ReadAllText(repoFile relative)
