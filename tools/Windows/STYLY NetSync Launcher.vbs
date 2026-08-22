' STYLY NetSync Launcher - double-click this file.
'
' It starts netsync-launcher.ps1 with the window hidden, so the setup GUI opens
' without a console window ever appearing. Everything else - installing uv,
' downloading the server, reporting errors - happens in that script's dialogs.

Option Explicit

Dim shell, fso, scriptDir, psScript, command

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
psScript = fso.BuildPath(scriptDir, "netsync-launcher.ps1")

If Not fso.FileExists(psScript) Then
    MsgBox "netsync-launcher.ps1 was not found next to this file." & vbCrLf & vbCrLf & _
           "Keep both files together in the same folder.", _
           vbCritical, "STYLY NetSync Launcher"
    WScript.Quit 1
End If

command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & psScript & """"

' 0 = hidden window, False = do not wait for it to finish.
shell.Run command, 0, False
