# GoreBox

Настоящий proxy/VPN-клиент на C# (.NET 8, WPF): обвязка над ядром **nekobox_core**
(sing-box из исходников nekoray, vendored в `core/`).
## Протоколы
Профили: `vmess`, `vless`, `trojan`, `shadowsocks`, `socks`, `http`, `hysteria2`,
`hysteria`, `tuic`, `anytls`, `ssh`, `wireguard`, `amneziawg`, `mtproto`, `tunnel` (custom JSON).

| Требование | Как реализовано |
|---|---|
| vmess / vless / trojan / shadowsocks / http | outbounds ядра sing-box (сборка без extra-тегов) |
| wireguard | outbounds (старые ядра) или `endpoints` (≥1.11); MTU clamp, auto_route/strict_route в TUN |
| hysteria / hysteria2 / tuic | тег `with_quic` (vendored sing-quic) |
| mixed | inbound mixed — режим прослушки по умолчанию (Socks+Http) |
| tunnel | профиль Custom: JSON ядра пользователя идёт в ядро как есть (ветка RawJson в SingBoxConfigBuilder) |
| tun | режим TUN (маршрут по умолчанию, stack/gvisor из тегов сборки) |
| mtproto | применяется только внутри Telegram (ядро sing-box MTProto-outbound не имеет ни в одной ветке) — профиль открывается через `tg://` |
| amneziawg | расширенные сборки ядра (sing-box-extended, sing-box-lx и т.п.); базовая сборка отклоняет профиль с подсказкой |

Со стороны UI список протоколов — `ProfileEditorViewModel.ProtocolList`, конвертация в JSON
ядра — `Services/SingBoxConfigBuilder.cs`, импорт ссылок/подписок/JSON — `Utils/LinkParser.cs`
и `Utils/JsonConfigConverter.cs`.

## Архитектура: gRPC поверх nekobox_core

`nekobox_core` — один бинарник с двумя режимами (см. `core/README.md`):

1. **gRPC (основной для GoreBox):** `sing-box.exe nekobox --token T --port P` поднимает
   libcore-сервис на 127.0.0.1; приложение шлёт `Start` (конфиг-JSON), `Stop`, `Test`,
   `QueryStats` с заголовком `nekoray_auth` (ровно одно значение). Клиент —
   `src/GoreBox/Services/NekoCoreRpcClient.cs`, протокол — `src/GoreBox/Protos/libcore.proto`
   (зеркало `core/nekoray/go/grpc_server/gen/libcore.proto`; генерация стабов — Grpc.Tools).
2. **Fallback `run -c`:** если ядро без баннера `NekoBox:` (скачанный релиз, форк) или
   gRPC-путь по любой причине не поднялся, `CoreService.StartAsync` автоматически запускает
   тот же бинарник обычным `run -c … -D run\` — контракт с UI не меняется.

Статистика трафика и задержка идут через clash API из конфига (он поднимается внутри
инстанса в обоих режимах); `QueryStats` gRPC доступен как альтернатива.

## Хранение данных
Профили, наборы правил, настройки, ядро, логи и профиль браузера «Панели» лежат в одной папке.
По умолчанию это папка `data` рядом с `GoreBox.exe`. Место выбирается в «Настройках» → «Данные приложения»:
«В локальной папке» (`%LocalAppData%\GoreBox`), «Рядом с исполняемым файлом» или «Указать своё место».
Выбор запоминается в `%LocalAppData%\GoreBox\location.json` и вступает в силу после перезапуска.
Если в новой папке данных ещё нет, данные из прежней копируются при запуске; существующие файлы не перезаписываются.
Если папка рядом с exe недоступна для записи (например, в `Program Files`), используется локальная папка и приложение об этом сообщает.
