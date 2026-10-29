' 설치된 garmin-box EXE의 --sync를 창 없이 실행하는 수동 런처. 결과는 EXE가 보여 준다.
Set shell = CreateObject("WScript.Shell")
exe = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\garmin-box\GarminBox.exe"
If CreateObject("Scripting.FileSystemObject").FileExists(exe) Then
  shell.Run """" & exe & """ --sync", 0, False
Else
  MsgBox "garmin-box is not installed. Run GarminBox.exe from the release first.", 48, "garmin-box"
End If
