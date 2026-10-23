# Bosch BES2 Windows: подтверждённое чтение Intuvia

23.10.2026 на физическом Intuvia `108C:0155` получен ответ `62 02 20 05 09 02 00`: **версия ПО 5.9.2.0**. Исправлены начальный sequence SWA и смещение CAN-записи. [Техническое исследование](BES2-TRANSPORT-FINDINGS.md).

Запустите **BoschBES2Auto.exe** и нажмите **ЗАПУСТИТЬ АВТОМАТИЧЕСКУЮ ДИАГНОСТИКУ**. Программа проверит USB, согласует режим, выполнит одно чтение BUI и сохранит JSON в `reports/` рядом с EXE. Одна кнопка выполняет один сеанс; после ответа поток закрывается. Python и отдельно установленный .NET для самостоятельного EXE не требуются.

В интерфейсе есть остановка, HEX/API-лог, уровни USB/транспорт/сессия/ECU, выбор задержки 0–3000 мс и сравнение JSON. Запись параметров, сбросы, SecurityAccess, команды движения и перебор адресов отсутствуют. Текущий подтверждённый объём — версия ПО BUI; двигатель и батарея пока не проверяются.

## Командная строка

```
BoschBES2Auto.exe --enumerate --report-dir C:\Reports
BoschBES2Auto.exe --probe --delay-ms 0 --report-dir C:\Reports
```

`--enumerate` не открывает поток и не пишет в устройство. `--probe` отправляет handshake и не более одного известного запроса чтения. Коды выхода: 0 — перечисление/подтверждённое чтение; 2 — неверные аргументы; 3 — чтение не подтверждено; 4 — ошибка сохранения; 130 — отмена.

Это GUI EXE: для автоматизации из PowerShell используйте `Start-Process -Wait -PassThru` с перенаправлением stdout/stderr. Именно этот способ проверен на готовом самостоятельном EXE; отображение Console.WriteLine и Ctrl+C в обычной интерактивной консоли не гарантируется. В интерфейсе отмена доступна кнопкой СТОП.

## Сборка и тесты

```
dotnet run --project windows/BoschBES2.Tests -c Release
dotnet run --project windows/BoschDiagnostic.Tests -c Release
dotnet run --project windows/BoschSniffer.Tests -c Release
dotnet publish windows/BoschBES2Auto -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o windows-dist
```

Workflow **Bosch BES2 Windows EXE** собирает оба EXE и запускает три набора тестов. Артефакт: **Bosch-BES2-Windows-USBPcap-Sniffer**. Сниффер требует отдельно установленный USBPcap; тестирование реального захвата в этом исследовании не выполнено, поскольку драйвер не установлен.

Полные отчёты могут содержать идентификаторы и остаток USB-буфера. Программа сохраняет их локально; перед публикацией нужна проверка. Публичные тестовые фикстуры очищены от хвоста реальной памяти.
