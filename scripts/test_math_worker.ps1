$json = '{"$type":"equation","Left":{"$type":"binaryOp","Operator":"+","Left":{"$type":"binaryOp","Operator":"*","Left":{"$type":"number","Value":2},"Right":{"$type":"variable","Name":"x"}},"Right":{"$type":"number","Value":5}},"Right":{"$type":"number","Value":15}}'
$tmp = New-TemporaryFile
Set-Content -Path $tmp -Value $json -Encoding UTF8
$content = Get-Content -Raw $tmp
& .\flinch\.venv\Scripts\python.exe src\Flinch.MathWorker\solver.py $content
Remove-Item $tmp
