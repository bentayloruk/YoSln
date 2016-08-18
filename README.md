# YoSln

* Do you want to rename a F#, C# or VB.NET project and have it move folders too?
* Do you want to update all the referencing projects and solutions?
* Do you like Netflix's [Stranger Things](https://www.netflix.com/gb/title/80057281)?
* Are you comfortable on the A road to the danger zone?

If you answered yes to all of the above *and* you are drunk, this may be the project for you.

**This is a 0.1, which means it WORKS FOR ME AND MY NEED. Your mileage will probably vary.**

This is a command line tool and is written in F#.

*Really, still here?!*

OK.

## Usage

### Move a project

You would do a `moveproj` like this:

`yosln.exe moveproj --projectname src\SuckyMcSuckName\SuckyMcSuckName.csproj --newname SuperRadName`

You would end up with:

* `src\SuperRadName\SuperRadName.csproj`.
* All projects and solutions below `.` (current directory) having their references updated.

