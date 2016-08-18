open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open System.Xml

type MoveProjInput =
    { SrcProjFileInfo : FileInfo
      DestProjName : string
      RenameFolder : bool
      SlnDir : DirectoryInfo }

type ProjMoveInfo = ProjMoveInfo of srcPath:string * Guid * destPath:string
            
///
/// Handle program arguments.
///
module Args = 

    open Argu

    type private MoveProjArgs =
        | [<Mandatory; Unique; AltCommandLine("-pp", "--project-path")>] Proj_Path of string
        | [<Mandatory; Unique; AltCommandLine("-nn")>] New_Name of string
        | [<Unique; AltCommandLine("-sd", "--soludion-dir")>]Sln_Dir of string
        | [<Unique; AltCommandLine("-drf")>] Dont_Rename_Folder
    with
        interface IArgParserTemplate with
            member x.Usage =
                match x with
                | Proj_Path(_) -> "Relative or absolute path of project to rename." 
                | New_Name(_) -> "New name for project." 
                | Sln_Dir(_) -> "Directory to recursively search in order to update project and solution references." 
                | Dont_Rename_Folder(_) -> "Flag to indicate that you don't want the project folder moved." 

    and private SlnToolArgs =
        | [<CliPrefix(CliPrefix.None)>] MoveProj of ParseResults<MoveProjArgs>
        with
            interface IArgParserTemplate with
                member x.Usage =
                    match x with
                    | MoveProj (_) -> "Move a project by giving it a new name." 


    let private ensurePathRooted (path:string) =
        if Path.IsPathRooted(path) then path
        else Path.Combine(Environment.CurrentDirectory, path)

    let private processProjPath arg =
        let projPath = ensurePathRooted arg
        if File.Exists(projPath) then FileInfo(projPath) else failwithf "Project %s does not exist." projPath

    let private processSlnDir arg =
        let slnPath = ensurePathRooted arg
        if Directory.Exists(slnPath) then DirectoryInfo(slnPath) else failwithf "Solution directory %s does not exist." slnPath 

    let processNewName arg = arg // TODO research project name limitations.

    let private processMoveProjArgs (results:ParseResults<MoveProjArgs>) =
        let projPath = results.PostProcessResult(<@ Proj_Path @>, processProjPath)
        let newName = results.PostProcessResult(<@ New_Name @>, processNewName)
        let slnDir = 
            match results.TryPostProcessResult(<@ Sln_Dir @>, processSlnDir) with
            | Some path -> path
            | None -> DirectoryInfo(Environment.CurrentDirectory)
        { SrcProjFileInfo = projPath
          DestProjName = newName
          RenameFolder = not (results.Contains(<@ Dont_Rename_Folder@>))
          SlnDir = slnDir }
            
    let parseArgsOrThrow (args) =
        let programName = Reflection.Assembly.GetEntryAssembly().GetName().Name + ".exe"
        let argParser = Argu.ArgumentParser.Create<SlnToolArgs>(programName = programName.ToLower())
        let results = argParser.Parse(args)
        results.PostProcessResult(<@ SlnToolArgs.MoveProj @>, processMoveProjArgs)
    

///
/// Handle MSBuild stuff.
///
module ProjectSystem = 

    let private projExtensions = [".csproj"; ".fsproj"; ".vbproj" ]

    let getAllProjectPaths (searchPath : string) =
        [ for projExtension in projExtensions do
            let projSearchPattern = sprintf "*%s" projExtension
            yield! Directory.GetFiles(searchPath, projSearchPattern, SearchOption.AllDirectories ) ] 


///
/// Path extensions.
///
module Path =

    // Get the relative path between two files.
    let makeRelativePath (src:FileInfo) (dest:FileInfo) =
        // Note: Using FileInfo so there is no confusion on string path being dir or file.
        // Uri requires trailing slash on dir, but win Path functions don't do this.
        let destUri = Uri(dest.FullName)
        let srcUri = Uri(src.FullName)
        let diffUri = srcUri.MakeRelativeUri(destUri)
        diffUri.OriginalString.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)


///
/// The program!
///
[<EntryPoint>]
let main argv = 

    // TODO?
    // * Better regex for solution update (e.g. confirm project Guid).
    // * Optionally update doc output xml and assembly name?  I didn't need this :-)
    // * Rename .suo files.
    // * Rename R# etc files.
    // * Search for other path refs in files?  Print them as warning?
    // * Look for InternalsVisibleTo refs.

    try
        // Parse our input or die.
        let input = Args.parseArgsOrThrow argv
        let srcProjDirInfo = input.SrcProjFileInfo.Directory

        // Figure out info of projects that will move.
        let projMoveInfos =

            let destDirName =
                if input.RenameFolder then
                    Path.Combine(srcProjDirInfo.Parent.FullName, input.DestProjName)
                else srcProjDirInfo.FullName

            // If renaming the folder, check for multiple projects.  Otherwise, just the src proj.
            let projPathsOnTheMove = 
                if input.RenameFolder then ProjectSystem.getAllProjectPaths srcProjDirInfo.FullName
                else [ input.SrcProjFileInfo.FullName]

            [ for projPath in projPathsOnTheMove do
                let projFileInfo = FileInfo(projPath)
                let projGuid = 
                    let regex = Regex("<ProjectGuid>(.*?)</ProjectGuid>")
                    let m = regex.Match(File.ReadAllText(projPath));
                    Guid(m.Groups.[1].Value)
                let destProjPath =
                    if projFileInfo.FullName = input.SrcProjFileInfo.FullName then
                        let destProjFilename = sprintf "%s%s" input.DestProjName input.SrcProjFileInfo.Extension 
                        Path.Combine(destDirName, destProjFilename)
                    else Path.Combine(destDirName, projFileInfo.Name)

                yield ProjMoveInfo(projPath, projGuid, destProjPath)
            ]


        // Fix the project references.  They look like this:
        // <ProjectReference Include="..\Enticify\Enticify-MS.csproj">
        //   <Project>{edef2de5-f23c-4fdb-92eb-261503000b75}</Project>
        //   <Name>Enticify-MS</Name>
        // </ProjectReference>
        for projPath in ProjectSystem.getAllProjectPaths (input.SlnDir.FullName) do
            let projXDoc = XDocument.Load(projPath, LoadOptions.PreserveWhitespace)

            // List the ref include values.
            let projRefElemsToUpdate =
                let projRefElems = projXDoc.Descendants(XName.Get("ProjectReference", "http://schemas.microsoft.com/developer/msbuild/2003")) |> Seq.toList
                [ for projRefElem in projRefElems do 
                    let projRefIncludeFileInfo =
                        let projDirName = Path.GetDirectoryName(projPath)
                        let relativePath = projRefElem.Attribute(XName.Get("Include")).Value
                        let absolutePath = Path.Combine(projDirName, relativePath) 
                        FileInfo(absolutePath)
                    for ProjMoveInfo(srcPath, projGuid, destPath) in projMoveInfos do
                        let refGuid = Guid(projRefElem.Element(XName.Get("Project", "http://schemas.microsoft.com/developer/msbuild/2003")).Value)
                        if refGuid = projGuid then
                            let newInclude = Path.makeRelativePath (FileInfo(projPath)) (FileInfo(destPath))
                            yield (newInclude, projRefElem)
                ]

            // Update the ref include values.
            match projRefElemsToUpdate with
            | [] -> ()
            | refsToUpdate ->
                for (newIncludeValue, refElem) in refsToUpdate do
                    refElem.Attribute(XName.Get("Include")).SetValue(newIncludeValue)
                use writer = new XmlTextWriter(projPath, new Text.UTF8Encoding(true))
                projXDoc.Save(writer)


        // Update any broken solution file refs.
        let slnPaths = Directory.GetFiles(input.SlnDir.FullName, "*.sln", SearchOption.AllDirectories)
        for slnPath in slnPaths do
            let rec updateSlnRefs slnText (projMoveInfos:ProjMoveInfo list) = 
                match projMoveInfos with
                | [] -> slnText
                | (ProjMoveInfo(srcPath, guid, destPath))::t -> 
                    let srcRelativePath = Path.makeRelativePath (FileInfo(slnPath)) (FileInfo(srcPath))
                    let destRelativePath = Path.makeRelativePath (FileInfo(slnPath)) (FileInfo(destPath))
                    let regex = Regex.Escape(sprintf "\"%s\"" srcRelativePath)
                    let slnText = Regex.Replace(slnText, regex, sprintf "\"%s\"" destRelativePath)
                    updateSlnRefs slnText t
            let slnText = File.ReadAllText(slnPath)
            let updatedSlnText = updateSlnRefs slnText projMoveInfos
            // MAYBE don't write if not changed.
            File.WriteAllText(slnPath, updatedSlnText, Text.UTF8Encoding(true))


        // Better do the actual rename!
        let destProjFilename = sprintf "%s%s" input.DestProjName input.SrcProjFileInfo.Extension 
        let destProjPath = Path.Combine(srcProjDirInfo.FullName, destProjFilename)
        File.Move(input.SrcProjFileInfo.FullName, destProjPath)

        // And maybe the move.
        if input.RenameFolder then
            let destDirPath = Path.Combine(srcProjDirInfo.Parent.FullName, input.DestProjName)
            Directory.Move(srcProjDirInfo.FullName, destDirPath)

        0

    with
    | x ->
        printfn "%s\n%s"  x.Message x.StackTrace
        1
