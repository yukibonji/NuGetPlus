﻿module NuGetPlus.ProjectManagement

open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Text.RegularExpressions
open System.Xml.Linq
open NuGet
open Microsoft.Build.Evaluation

let inline private grabCompatible (project : IProjectSystem) f name = 
    match project.TryGetCompatibleItems(f()) with
    | (true, result) -> result
    | (false, _) -> Seq.empty

let private getFilteredAssemblies (package : IPackage) (project : IProjectSystem) assemblyReferences = 
    match package.PackageAssemblyReferences with
    | null -> assemblyReferences
    | x when (Seq.isEmpty x) -> assemblyReferences
    | par -> 
        match project.TryGetCompatibleItems(par) with
        | (true, items) -> 
            Seq.filter 
                (fun (assembly : IPackageAssemblyReference) -> 
                    Seq.exists
                        (fun (pr : PackageReferenceSet) -> 
                            pr.References.Contains
                                (assembly.Name, 
                                StringComparer.OrdinalIgnoreCase)) items) 
                assemblyReferences
        | (false, _) -> assemblyReferences

let private fileTransformers : IDictionary<string, IPackageFileTransformer> = 
    dict [(".transform", XmlTransformer() :> IPackageFileTransformer);
          (".pp", Preprocessor() :> IPackageFileTransformer)]

let private getFiles project (package : IPackage) = 
    let assemblyReferences = 
        grabCompatible project (fun () -> package.AssemblyReferences)
            "assembly references"
    let frameworkReferences = 
        grabCompatible project (fun () -> package.FrameworkAssemblies) 
            "framework assemblies"
    let contentFiles = 
        grabCompatible project (package.GetContentFiles) "content files"
        |> Seq.filter (fun p -> p.EffectivePath <> null && p.TargetFramework <> null)
    let buildFiles = 
        grabCompatible project (package.GetBuildFiles) "build files"
    assemblyReferences, frameworkReferences, contentFiles, buildFiles

let private RemoveFilesFromProj packageInstallPath (package : IPackage) 
    (project : IProjectSystem) (localRepo : IPackageRepository) = 
    let packagesConfig = "packages.config"
    let otherPackages = 
        (XDocument.Parse <| project.OpenFile(packagesConfig).ReadToEnd())
            .Descendants(XName.Get "package")
        |> Seq.filter(fun p -> p.Attribute(XName.Get "id").Value <> package.Id)
        |> Seq.map
               (fun p -> 
                   localRepo.FindPackage
                       (p.Attribute(XName.Get "id").Value, 
                        
                        new SemanticVersion(p.Attribute(XName.Get "version").Value), 
                        true, true))
        |> Seq.filter(fun p -> p <> null)
        |> Seq.toList
    let inUseByOtherPackages = 
        if List.length otherPackages > 0 then 
            otherPackages
            |> Seq.map(fun p -> getFiles project p)
            |> Seq.reduce
                   (fun acc next -> 
                       let a, f, c, b = acc
                       let a', f', c', b' = next
                       (Seq.append a a', Seq.append f f', Seq.append c c', 
                        Seq.append b b'))
            |> fun (a, f, c, b) -> 
                
                Seq.distinctBy 
                    (fun (assembly : IPackageAssemblyReference) -> assembly.Name) 
                    a, 
                
                Seq.distinctBy 
                    (fun (frameworkRef : FrameworkAssemblyReference) -> 
                        frameworkRef.AssemblyName) f, 
                
                c
                |> Seq.filter
                       (fun (contentFile : IPackageFile) -> 
                           not 
                           <| fileTransformers.ContainsKey
                                  (Path.GetExtension contentFile.EffectivePath))
                |> Seq.distinctBy
                       (fun contentFile -> 
                           let path =
                                if String.IsNullOrEmpty contentFile.EffectivePath then "" else contentFile.EffectivePath
                           let version =
                                if contentFile.TargetFramework <> null then contentFile.TargetFramework.Version else Version()
                           path, version), 
                Seq.distinctBy 
                    (fun (buildFile : IPackageFile) -> 
                        buildFile.EffectivePath, 
                        buildFile.TargetFramework.Version) b
        else (Seq.empty, Seq.empty, Seq.empty, Seq.empty)
    let (assemblyReferencesToDelete, frameworkReferencesToDelete, 
         contentFilesToDelete, buildFilesToDelete) = 
        let a, f, c, b = getFiles project package
        let a', f', c', b' = inUseByOtherPackages
        
        Seq.filter 
            (fun (assembly : IPackageAssemblyReference) -> 
                Seq.exists 
                    (fun (assembly' : IPackageAssemblyReference) -> 
                        assembly.Name = assembly'.Name) a' |> not) a, 
        
        Seq.filter 
            (fun (assembly : FrameworkAssemblyReference) -> 
                Seq.exists 
                    (fun (assembly' : FrameworkAssemblyReference) -> 
                        assembly.AssemblyName = assembly'.AssemblyName) f' 
                |> not) f, 
        
        Seq.filter 
            (fun (contentFile : IPackageFile) -> 
                Seq.exists 
                    (fun (contentFile' : IPackageFile) -> 
                        (contentFile.EffectivePath, 
                         contentFile.TargetFramework.Version) = (
                                                                 contentFile'.EffectivePath, 
                                                                 
                                                                 contentFile'.TargetFramework.Version)) 
                    c' |> not) c, 
        
        Seq.filter 
            (fun (buildFile : IPackageFile) -> 
                Seq.exists 
                    (fun (buildFile' : IPackageFile) -> 
                        (buildFile.EffectivePath, 
                         buildFile.TargetFramework.Version) = (
                                                               buildFile'.EffectivePath, 
                                                               
                                                               buildFile'.TargetFramework.Version)) 
                    b' |> not) b
    
    let mappedFileTransformers = fileTransformers |> Seq.map(fun (KeyValue(k,v)) -> new FileTransformExtensions(k, k), v)  |> dict
         
    project.DeleteFiles(contentFilesToDelete, otherPackages, mappedFileTransformers)
    assemblyReferencesToDelete 
    |> Seq.iter(fun a -> project.RemoveReference(a.Name))
    buildFilesToDelete
    |> Seq.map(fun bf -> Path.Combine(packageInstallPath, bf.Path))
    |> Seq.iter(fun fullPath -> project.RemoveImport(fullPath))

let private AddFilesToProj packageInstallPath package project = 
    let assemblyReferences, frameworkReferences, contentFiles, buildFiles = 
        getFiles project package
    if (Seq.isEmpty assemblyReferences && Seq.isEmpty frameworkReferences 
        && Seq.isEmpty contentFiles && Seq.isEmpty buildFiles) 
       && not
              (Seq.isEmpty package.AssemblyReferences 
               && Seq.isEmpty package.FrameworkAssemblies 
               && Seq.isEmpty(package.GetContentFiles()) 
               && Seq.isEmpty(package.GetBuildFiles())) then 
        failwith 
            "Unable to find compatible items for framework %s in package %s." 
            (project.TargetFramework.FullName) (package.GetFullName())
    let filteredAssemblyReferences = 
        getFilteredAssemblies package project assemblyReferences
    let mappedFileTransformers = fileTransformers |> Seq.map(fun (KeyValue(k,v)) -> new FileTransformExtensions(k, k), v)  |> dict
    project.AddFiles(contentFiles, mappedFileTransformers)
    frameworkReferences
    |> Seq.filter(fun f -> not <| project.ReferenceExists(f.AssemblyName))
    |> Seq.iter(fun f -> project.AddFrameworkReference(f.AssemblyName))
    filteredAssemblyReferences
    |> Seq.filter(fun a -> not <| a.IsEmptyFolder())
    |> Seq.iter(fun a -> 
               let refPath = Path.Combine(packageInstallPath, a.Path)
               if project.ReferenceExists(a.Name) then 
                   project.RemoveReference(a.Name)
               project.AddReference(refPath, Stream.Null))

let private SortPackages(configDoc : XDocument) = 
    configDoc.Element(XName.Get "packages").Elements(XName.Get "package")
    |> Seq.sortBy(fun p -> (p.Attribute(XName.Get "id")).Value.ToLower())
    |> Seq.map(fun x -> 
               x.Remove()
               x)
    |> Seq.toArray
    |> configDoc.Element(XName.Get "packages").Add

let private DeletePackageNode id (packagesNode : XElement) = 
    packagesNode.Elements()
    |> Seq.tryFind
           (fun e -> 
               e.Attribute(XName.Get "id") <> null 
               && (e.Attribute(XName.Get "id")).Value = id)
    |> function 
       | Some node -> node.Remove()
       | None -> ()

let private UninstallFromPackagesConfigFile id (project : IProjectSystem) = 
    let fileName = "packages.config"
    if project.FileExists(fileName) then 
        project.OpenFile(fileName)
        |> fun stream -> XDocument.Parse(stream.ReadToEnd())
        |> Some
    else None
    |> function 
       | None -> ()
       | Some configDoc -> 
           configDoc.Element(XName.Get "packages") |> DeletePackageNode id
           SortPackages configDoc
           project.AddFile(fileName, fun (s : Stream) -> configDoc.Save(s))

let HasReferenceToPackage id project =
    let config = Path.Combine(Path.GetDirectoryName project, "packages.config")
    if File.Exists config then
        File.ReadAllText(config)
        |> XDocument.Parse
        |> (fun configDoc ->
                configDoc.Element(XName.Get "packages").Elements()
                |> Seq.exists (fun x -> x.Attribute(XName.Get "id") <> null && x.Attribute(XName.Get "id").Value = id))
    else
        false

let private InstallToPackagesConfigFile (package : IPackage) 
    (project : IProjectSystem) = 
    let fileName = "packages.config"
    let configDoc = 
        use stream = 
            if not <| project.FileExists(fileName) then 
                using (new IO.StreamWriter(project.CreateFile(fileName))) 
                    (fun writer -> writer.Write("<packages />"))
            project.OpenFile(fileName)
        XDocument.Parse(stream.ReadToEnd())
    let packagesNode = configDoc.Element(XName.Get "packages")
    // Check if a version is already installed, and remove it...
    DeletePackageNode package.Id packagesNode
    // Add the new version
    let packageNode = 
        sprintf "<package id=\"%s\" version=\"%s\" targetFramework=\"%s\" />" 
            package.Id (package.Version.ToString()) 
            (VersionUtility.GetShortFrameworkName project.TargetFramework) 
        |> XElement.Parse
    packagesNode.Add packageNode
    SortPackages configDoc
    project.AddFile(fileName, fun (s : Stream) -> configDoc.Save(s))

let private GetManager projectName = 
    let projectDir = Path.GetFullPath <| IO.Path.GetDirectoryName projectName
    let settings = Settings.LoadDefaultSettings(PhysicalFileSystem projectDir,null,null)
    let packageManager = GetRawManager (GetRepositoryPath projectName) settings
    let project = ProjectSystem(projectName) :> IProjectSystem
    packageManager.PackageInstalling.Add
        (fun ev -> InstallToPackagesConfigFile ev.Package project)
    packageManager.PackageInstalling.Add
        (fun ev -> AddFilesToProj ev.InstallPath ev.Package project)
    packageManager.PackageUninstalling.Add
        (fun ev -> 
            RemoveFilesFromProj ev.InstallPath ev.Package project 
                packageManager.LocalRepository)
    packageManager.PackageUninstalling.Add
        (fun ev -> UninstallFromPackagesConfigFile ev.Package.Id project)
    packageManager

let GetRestorePackages projectName = 
    let packagesConfig = 
        Path.Combine(Path.GetDirectoryName(projectName), "packages.config")
    if File.Exists packagesConfig then 
        use stream = File.OpenRead(packagesConfig)
        try
            XDocument.Load(stream).Element(XName.Get "packages")
                     .Elements(XName.Get "package") 
            |> Seq.map
                   (fun p -> 
                       { Id = p.Attribute(XName.Get "id").Value;
                         Version = 
                             SemanticVersion(p.Attribute(XName.Get "version").Value) })
        with
        | e ->
            failwithf "Sorry, there was error reading %s" packagesConfig
    else Seq.empty

let UpdateReferenceToSpecificVersion projectName packageId 
    (version : SemanticVersion) = 
    let pm = GetManager projectName
    let existingPackage = pm.LocalRepository.FindPackage(packageId)
    pm.UninstallPackage(existingPackage, true, true)
    pm.InstallPackage(packageId, version, false, true)

let UpdateReference projectName (packageId : string) = 
    let pm = GetManager projectName
    let existingPackage = pm.LocalRepository.FindPackage(packageId)
    pm.UninstallPackage(existingPackage, true, true)
    pm.InstallPackage packageId

let rec private installCachedDependency projectName (manager : PackageManager) 
        (dependency : PackageDependency) = 
    // reuse install dependencies if they exist; probably what's wanted
    let matchingLocal = 
        manager.LocalRepository.FindPackage
            (dependency.Id, dependency.VersionSpec, true, true)
    let installPackage = 
        if matchingLocal <> null then matchingLocal
        else 
            manager.SourceRepository.FindPackage
                (dependency.Id, dependency.VersionSpec, true, true)
    if installPackage = null then 
        failwith "No candidate found to match dependency %s %A" dependency.Id 
            dependency.VersionSpec
    installSpecificVersion projectName installPackage.Id installPackage.Version 
        false

and private installSpecificVersion projectName packageId 
    (version : SemanticVersion) ignoreDeps = 
    let manager = GetManager projectName
    let ok, package = manager.LocalRepository.TryFindPackage(packageId, version)
    if ok then 
        manager.Logger.Log
            (MessageLevel.Info, 
             "LocalRepository already contains package {0}, installing files.", 
             packageId)
        let project = ProjectSystem(projectName) :> IProjectSystem
        InstallToPackagesConfigFile package project
        AddFilesToProj (manager.PathResolver.GetInstallPath package) package 
            project
        if not ignoreDeps then 
            package.DependencySets
            |> Seq.map(fun depSet -> depSet.Dependencies)
            |> Seq.concat
            |> Seq.iter
                   (fun dep -> installCachedDependency projectName manager dep)
    else manager.InstallPackage(packageId, version, ignoreDeps, true)

let InstallReferenceOfSpecificVersion projectName packageId version = 
    installSpecificVersion projectName packageId version false
let InstallReferenceOfSpecificVersionNoDependencies projectName packageId 
    version = installSpecificVersion projectName packageId version true

let InstallReference projectName packageId = 
    let manager = GetManager projectName
    let latest = 
        manager.SourceRepository.FindPackage
            (packageId, VersionSpec(), false, false)
    let ok, package = 
        manager.LocalRepository.TryFindPackage(latest.Id, latest.Version)
    if ok then 
        manager.Logger.Log
            (MessageLevel.Info, 
             "LocalRepository already contains package {0}, installing files.", 
             packageId)
        let project = ProjectSystem(projectName) :> IProjectSystem
        InstallToPackagesConfigFile package project
        AddFilesToProj (manager.PathResolver.GetInstallPath package) package 
            project
    else manager.InstallPackage packageId

let RestoreReferences projectName = 
    let projectDir = Path.GetFullPath <| IO.Path.GetDirectoryName projectName
    let settings = Settings.LoadDefaultSettings(PhysicalFileSystem projectDir, null, null)
    let manager = GetRawManager (GetRepositoryPath projectName) settings
    let packages = GetRestorePackages projectName
    packages 
    |> Seq.iter(fun p -> manager.InstallPackage(p.Id, p.Version, false, true))

let RemoveReference projectName (packageId : string) = 
    let manager = GetManager projectName
    let restorePackages = GetRestorePackages projectName
    let version = (Seq.find (fun p -> p.Id = packageId) restorePackages).Version
    let package = manager.LocalRepository.FindPackage(packageId, version)
    manager.UninstallPackage(package, true, true)