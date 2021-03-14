﻿[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FSharpApiSearch.Database

open System.IO
open MessagePack.Resolvers
open MessagePack
open MessagePack.FSharp
open FSharp.Control.Tasks.Affine

let databaseName = "database"

module internal CompactImpl =
  open System.Collections.Generic

  let visit (assemblies: HashSet<string>) (t: LowType) =
    let rec collectAssemblyName = function
      | Wildcard _ -> ()
      | Variable _ -> ()
      | Identifier (ConcreteType c, _) -> assemblies.Add(c.AssemblyName) |> ignore
      | Identifier (UserInputType _, _) -> ()
      | Arrow ((ps, ret), _) -> List.iter collectAssemblyName ps; collectAssemblyName ret
      | Tuple (t, _) -> List.iter collectAssemblyName t.Elements
      | Generic (id, args, _) -> collectAssemblyName id; List.iter collectAssemblyName args
      | TypeAbbreviation (t, _) -> collectAssemblyName t.Abbreviation; collectAssemblyName t.Original
      | Delegate (d, (ps, ret), _) -> collectAssemblyName d; List.iter collectAssemblyName ps; collectAssemblyName ret
      | ByRef (_, t, _) -> collectAssemblyName t
      | Subtype _ | Choice _ | LoadingType _ -> ()
    collectAssemblyName t
    t

  let rec collectUsedAssemblyNames (database: Database) (usedAssemblies: Set<string>) (testAssemblies: Set<string>) : Set<string> =
    testAssemblies
    |> Seq.fold (fun state testAssemblyName ->
      if state.Contains(testAssemblyName) then
        state
      else
        let before = state |> Set.add testAssemblyName
        let testAssembly = database |> Array.find (fun d -> d.AssemblyName = testAssemblyName)
        let after =
          let result = HashSet(before)
          testAssembly.Api |> Array.iter (LowTypeVisitor.accept_Api (visit result) >> ignore)
          Set(result)
        let newAssemblies = after - before
        collectUsedAssemblyNames database before newAssemblies
    ) usedAssemblies

  let removeUnusedAssembly (mainAssemblies: Set<string>) (database: Database) =
    let usedAssemblies = collectUsedAssemblyNames database Set.empty mainAssemblies
    database
    |> Array.filter (fun apiDict -> usedAssemblies.Contains(apiDict.AssemblyName))

  let implicitAssemblyIsOnlyTypeDef (mainAssemblies: Set<string>) (apiDict: ApiDictionary) =
    if mainAssemblies.Contains(apiDict.AssemblyName) then
      apiDict
    else
      { apiDict with
          Api = apiDict.Api |> Array.filter (fun api -> match api.Signature with ApiSignature.FullTypeDefinition _ | ApiSignature.TypeAbbreviation _ -> true | _ -> false)
      }
  let compact (assemblies: AssemblyLoader.AssemblyInfo[]) (database: Database) : Database =
    let mainAssemblies = assemblies |> Array.choose (fun x -> if not x.Implicit then Some x.Name else None) |> Set
    
    database
    |> Array.map (implicitAssemblyIsOnlyTypeDef mainAssemblies)
    |> removeUnusedAssembly mainAssemblies

let compact (assemblies: AssemblyLoader.AssemblyInfo[]) (database: Database) : Database = CompactImpl.compact assemblies database

module internal Serialization =
  type T = (string * Api[])[]
  let toDumpObj (xs: Database) : T = xs |> Array.map (fun x -> x.AssemblyName, x.Api)

  let fromDumpObj (xs: T) : Database =
    xs
    |> Array.map (fun (name, apis) ->
      { AssemblyName = name; Api = apis; TypeDefinitions = IDictionary.empty; TypeAbbreviations = Array.empty }
      |> ApiLoader.Impl.makeDefAndAbb
    )

let internal serializationOptions = lazy (
  let resolver = CompositeResolver.Create(FSharpResolver.Instance, StandardResolver.Instance)
  MessagePackSerializerOptions.Standard.WithResolver(resolver)
)

let saveStream (stream: Stream) (database: Database) cTok: System.Threading.Tasks.Task<unit> = task {
  return! MessagePackSerializer.SerializeAsync(stream, Serialization.toDumpObj database, serializationOptions.Value, cTok)
}

let save (path: string) (database: Database) ctok = task {
  if File.Exists(path) then File.Delete(path)
  use file = File.OpenWrite(path)
  return! saveStream file database ctok
}

let loadFromStream (stream: Stream) ctok = task {
  let! payload = MessagePackSerializer.DeserializeAsync<Serialization.T>(stream, serializationOptions.Value, ctok)
  return Serialization.fromDumpObj payload
}

let loadFromFile (path: string) ctok = task {
  use file = File.OpenRead(path)
  return! loadFromStream file ctok
}