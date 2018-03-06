﻿module FSharpApiSearch.AssemblyLoader

open Microsoft.FSharp.Compiler.SourceCodeServices
open System.IO
open System.Reflection

type AssemblyInfo = {
  Name: string
  Path: string
  Implicit: bool
}

type AssemblyResolver = {
  FSharpCore: string
  Framework: string list
  Directories: string list
}
with
  member this.TryResolve(assemblyName: string) : AssemblyInfo option =
    let assemblyName = if assemblyName.EndsWith(".dll") = false then assemblyName + ".dll" else assemblyName
    let result =
      if assemblyName = "FSharp.Core.dll" then
        Some (Path.Combine(this.FSharpCore, assemblyName))
      else
        seq { yield! this.Directories; yield! this.Framework }
        |> Seq.map (fun dir -> Path.Combine(dir, assemblyName))
        |> Seq.tryFindBack File.Exists
    result |> Option.map (fun path -> { Name = Path.GetFileNameWithoutExtension(path); Path = Path.GetFullPath(path); Implicit = false })

  member this.Resolve(assemblyName: string) : AssemblyInfo =
    match this.TryResolve(assemblyName) with
    | Some path -> path
    | None -> raise (FileNotFoundException("Assembly is not found.", assemblyName))

  member this.ResolveAll(assemblyNames: string seq) : AssemblyInfo[] =
    let mainAssemblies = assemblyNames |> Seq.map this.Resolve |> Seq.toArray

    let resolved = System.Linq.Enumerable.ToDictionary(mainAssemblies, fun a -> a.Name)

    let rec resolveImplicitReferences (assembly: AssemblyInfo) : unit =
      let m = Mono.Cecil.ModuleDefinition.ReadModule(assembly.Path)

      for reference in m.AssemblyReferences do
        let refName = reference.Name
        if resolved.ContainsKey(refName) = false then
          match this.TryResolve(refName) with
          | Some assembly ->
            let assembly = { assembly with Implicit = true }
            resolved.Add(refName, assembly)
            resolveImplicitReferences assembly
          | None -> ()
    
    mainAssemblies |> Array.iter resolveImplicitReferences

    resolved.Values
    |> Seq.toArray
    |> Array.sortBy (fun a -> a.Path)

let internal ignoreFSharpCompilerServiceError() =
  typeof<FSharpChecker>.Assembly.GetType("Microsoft.FSharp.Compiler.AbstractIL.Diagnostics")
  |> Option.ofObj
  |> Option.bind (fun diagMod -> diagMod.GetMember("diagnosticsLog", BindingFlags.NonPublic ||| BindingFlags.Static) |> Array.tryHead)
  |> Option.bind (tryUnbox<PropertyInfo>)
  |> Option.bind (fun x -> x.GetValue(null) |> Option.ofObj)
  |> Option.bind (tryUnbox<ref<Option<System.IO.TextWriter>>>)
  |> Option.iter (fun x -> x := None)

let load (references: AssemblyInfo[]) =
  ignoreFSharpCompilerServiceError()

  let checker = FSharpChecker.Create()
  let base1 = Path.GetTempFileName()
  let fileName1 = Path.ChangeExtension(base1, ".fs")
  let projFileName = Path.ChangeExtension(base1, ".fsproj")
  let dllName = Path.ChangeExtension(base1, ".dll")
  let options =
    checker.GetProjectOptionsFromCommandLineArgs
      (projFileName,
        [|
          yield "--simpleresolution" 
          yield "--noframework" 
          yield "--debug:full" 
          yield "--define:DEBUG" 
          yield "--optimize-" 
          yield "--out:" + dllName
          yield "--warn:3" 
          yield "--fullpaths" 
          yield "--flaterrors" 
          yield "--target:library" 
          yield fileName1

          for r in references do
            yield "-r:" + r.Path
        |]
      )
  let refAssemblies =
    let x = checker.ParseAndCheckProject(options) |> Async.RunSynchronously
    x.ProjectContext.GetReferencedAssemblies()
  Array.ofList refAssemblies