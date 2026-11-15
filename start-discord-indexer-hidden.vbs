Option Explicit
Dim shell, fso, root, target, command, code
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = root
If WScript.Arguments.Count > 1 Then WScript.Quit 2
If WScript.Arguments.Count = 1 Then
    target = fso.GetAbsolutePathName(WScript.Arguments(0))
    If Not fso.FileExists(target) Then WScript.Quit 2
    If LCase(fso.GetExtensionName(target)) <> "ps1" Then WScript.Quit 2
    command = Quote(shell.ExpandEnvironmentStrings("%SystemRoot%") & "\System32\WindowsPowerShell\v1.0\powershell.exe") & " -NoProfile -NonInteractive -WindowStyle Hidden -File " & Quote(target)
Else
    target = fso.BuildPath(root, "discord-indexer.exe")
    If Not fso.FileExists(target) Then WScript.Quit 2
    command = Quote(target)
End If
' Start hidden and wait so Task Scheduler tracks the real lifetime and exit code.
code = shell.Run(command, 0, True)
WScript.Quit code
Function Quote(value)
    Quote = Chr(34) & value & Chr(34)
End Function
' No credentials are read or written; the child inherits the process environment.
