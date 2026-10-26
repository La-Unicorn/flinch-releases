$res = Get-ItemProperty -Path "Registry::HKEY_CLASSES_ROOT\Interface\*" -ErrorAction SilentlyContinue | Where-Object { $_.'(default)' -like "*IUIAutomation*" }
$res | Select-Object PSChildName, '(default)'
