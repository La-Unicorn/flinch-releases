$cuia = Get-ItemProperty -Path "Registry::HKEY_CLASSES_ROOT\CLSID\*" -ErrorAction SilentlyContinue | Where-Object { $_.'(default)' -like "*UIAutomation*" }
$cuia | Select-Object PSChildName, '(default)'
