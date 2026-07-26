' garmin-box 배치를 창 없이 실행하는 런처. 출력은 batch.log에 남는다.
Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
CreateObject("WScript.Shell").Run _
  "powershell -ExecutionPolicy Bypass -File """ & scriptDir & "\run_garmin_batch.ps1""", 0, False
