' garmin-box 배치를 창 없이(hidden) 실행하는 런처.
' 작업 스케줄러가 이 .vbs를 호출하면 PowerShell 창이 뜨지 않는다 (출력은 batch.log에 남음).
' Run 인자: 명령, 0=숨김창, False=완료를 기다리지 않음.
CreateObject("WScript.Shell").Run _
  "powershell -ExecutionPolicy Bypass -File ""C:\Users\qoreh\git\garmin-box\run_garmin_batch.ps1""", 0, False
