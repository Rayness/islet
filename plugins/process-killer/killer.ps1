# Плагин Islet «Завершить процесс».
#
# Протокол: одна строка JSON на сообщение, в обе стороны (docs/plugins.md).
#   <- {"type":"query","id":1,"text":"chrome","scoped":true}
#   -> {"type":"results","id":1,"items":[...]}
#   <- {"type":"invoke","action":"kill","data":{"pid":123}}

# Без этого PowerShell читает stdin в кодировке консоли и кириллица превращается в кашу.
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8

$language = 'en'

function Send($object) {
    [Console]::Out.WriteLine(($object | ConvertTo-Json -Compress -Depth 6))
    [Console]::Out.Flush()
}

function T($en, $ru) { if ($language -eq 'ru') { $ru } else { $en } }

function Format-Memory([long]$bytes) {
    if ($bytes -ge 1GB) { return '{0:0.0} GB' -f ($bytes / 1GB) }
    return '{0:0} MB' -f ($bytes / 1MB)
}

function Answer($id, [string]$text) {
    $text = $text.Trim()
    $processes = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -ne 0 -and $_.Id -ne 4 -and $_.Id -ne $PID } |
        Where-Object { $text -eq '' -or $_.ProcessName -like "*$text*" -or ($_.MainWindowTitle -and $_.MainWindowTitle -like "*$text*") }

    # Одинаковые имена сворачиваем в одну строку: «chrome ×23 — 2,1 GB».
    $groups = $processes | Group-Object ProcessName |
        ForEach-Object {
            $memory = ($_.Group | Measure-Object WorkingSet64 -Sum).Sum
            [pscustomobject]@{ Name = $_.Name; Count = $_.Count; Memory = $memory; Group = $_.Group }
        } |
        Sort-Object Memory -Descending |
        Select-Object -First 10

    $items = @()
    foreach ($g in $groups) {
        $first = $g.Group | Select-Object -First 1
        $path = $null
        try { $path = $first.Path } catch { }
        $ids = @($g.Group | ForEach-Object { $_.Id })
        $count = if ($g.Count -gt 1) { " ×$($g.Count)" } else { '' }
        $windowTitle = ($g.Group | Where-Object { $_.MainWindowTitle } | Select-Object -First 1).MainWindowTitle

        $item = @{
            id       = $g.Name
            title    = "$($g.Name)$count"
            subtitle = if ($windowTitle) { $windowTitle } else { (T "PID $($ids[0])" "PID $($ids[0])") }
            trailing = Format-Memory $g.Memory
            glyph    = [string][char]0xE9D9
            confirm  = (T "Press Enter again to end $($g.Name)" "Нажмите Enter ещё раз, чтобы завершить $($g.Name)")
            action   = @{ invoke = 'kill'; data = @{ pids = $ids } }
        }
        if ($path) { $item.iconPath = $path }
        $items += $item
    }

    Send @{ type = 'results'; id = $id; items = $items }
}

function Kill($data) {
    $killed = 0
    foreach ($id in $data.pids) {
        try { Stop-Process -Id $id -Force -ErrorAction Stop; $killed++ } catch { }
    }
    $total = @($data.pids).Count
    if ($killed -eq $total) {
        Send @{ type = 'notify'; title = (T 'Process ended' 'Процесс завершён'); body = (T "$killed process(es) stopped" "Остановлено процессов: $killed"); glyph = [string][char]0xE9D9 }
    } else {
        Send @{ type = 'notify'; title = (T 'Could not end everything' 'Завершить удалось не всё'); body = (T "Stopped $killed of $total — the rest need administrator rights" "Остановлено $killed из $total — для остальных нужны права администратора"); glyph = [string][char]0xE7BA }
    }
}

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    if ($line.Trim() -eq '') { continue }
    try { $message = $line | ConvertFrom-Json } catch { continue }

    switch ($message.type) {
        'hello'    { if ($message.language) { $language = $message.language } }
        'query'    { Answer $message.id $message.text }
        'invoke'   { if ($message.action -eq 'kill') { Kill $message.data } }
        'shutdown' { exit 0 }
    }
}
