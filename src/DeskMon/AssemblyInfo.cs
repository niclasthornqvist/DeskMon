using System.Runtime.InteropServices;

// Every [DllImport] in DeskMon is looked up in System32 only - user32, kernel32,
// pdh, shell32, psapi, dwmapi and winsqlite3 all live there. The default search
// tries the program's own folder first, and DeskMon runs as administrator from
// a folder ordinary users can write to (app\ on E:), so a winsqlite3.dll
// dropped beside DeskMon.exe would have been loaded into an elevated process.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
