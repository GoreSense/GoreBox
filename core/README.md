# GoreBox — ядро (vendored)

Go-исходники ядра GoreBox: nekobox_core из архивного [MatsuriDayo/nekoray](https://github.com/MatsuriDayo/nekoray)
плюс его зависимости, зафиксированные через `replace` в go.mod. Всё лежит в дереве репозитория —
сборка ядра не требует клонирования внешних репозиториев, только Go 1.22+.

## Структура и пины

| Каталог | Происхождение | Коммит |
|---|---|---|
| `nekoray/go/cmd/nekobox_core` | MatsuriDayo/nekoray, точка входа ядра | `main` (архив) |
| `nekoray/go/cmd/updater` | то же (в сборку GoreBox не входит) | `main` |
| `nekoray/go/grpc_server` | gRPC-сервис libcore.proto (Start/Stop/Test/QueryStats) | `main` |
| `nekoray/LICENSE` | GPLv3 — см. «Лицензии» | — |
| `sing-box` | MatsuriDayo/sing-box (форк с boxapi/nekoutils) | `06557f6cef23160668122a17a818b378b5a216b5` |
| `sing-quic` | sagernet/sing-quic (QUIC для hysteria/hysteria2/tuic) | `b49ce60d9b3622d5238fee96bfd3c5f6e3915b42` |
| `libneko` | matsuridayo/libneko (логирование, speedtest, общие утилиты) | `1c47a3af71990a7b2192e03292b4d246c308ef0b` |

Пины взяты из `libs/get_source_env.sh` nekoray — это ровно те ревизии, под которые написан
`nekobox_core`. Версия sing-box в форке: `1.9.7-neko-1` (`sing-box/constant/version.go`).

## Сборка

Из корня репозитория (Windows):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-core.ps1 -Install
```

Эквивалент вручную (зеркало `libs/build_go.sh` nekoray), запускать из `core/nekoray/go/cmd/nekobox_core`:

```
CGO_ENABLED=0 go build -v -o sing-box.exe -trimpath ^
  -ldflags "-w -s -X github.com/matsuridayo/libneko/neko_common.Version_neko=GoreBox-1.2.0" ^
  -tags "with_clash_api,with_gvisor,with_quic,with_wireguard,with_utls,with_ech"
```

`replace`-директивы в `go.mod` nekobox_core указывают на соседние каталоги этого дерева
(`../../../../libneko`, `../../../../sing-box`, `../../../../sing-quic`, `../../grpc_server`),
поэтому network нужен только для прочих модулей из go.sum.

## Два режима работы одного бинарника

`nekobox_core` — обычный sing-box с одной добавкой (см. `go/cmd/nekobox_core/main.go`):

* `sing-box.exe run -c config.json -D dir` — стандартный режим, как у официального sing-box;
  его используют все «чужие» ядра (скачанные релизы, форки) и fallback GoreBox.
* `sing-box.exe nekobox --token T --port P` — gRPC-режим: слушает 127.0.0.1:P и принимает
  конфиг и команды (Start/Stop/Test/QueryStats) по `libcore.proto`; каждый запрос должен нести
  ровно одно значение заголовка `nekoray_auth` со значением T (см. `grpc_server/auth`).
  Так работает GoreBox со своей сборкой ядра (см. `src/GoreBox/Services/NekoCoreRpcClient.cs`).

Другие подкоманды (`version`, `check`, …) уходят в boxmain как обычно — все пробные проверки
ядра в приложении работают с любым бинарником.

RPC-метод `Update` ходит в релизы MatsuriDayo/nekoray — GoreBox его **не вызывает**; обновление
ядра у GoreBox своё (Настройки → Ядро).

## Лицензии

* `nekoray/` — GPLv3 (`nekoray/LICENSE`). Производный код, включая gRPC-обвязку GoreBox,
  распространяется на условиях GPLv3.
* `sing-box/`, `sing-quic/` — GPLv3 (LICENSE в каталогах).
* `libneko/` — **LICENSE-файла нет**: автор не разместил явную лицензию (только README).
  Формально это «all rights reserved»; исходники публичны, используются здесь на тех же
  условиях, на которых их использует upstream nekoray (GPLv3-программа). Если вам нужна
  безусловная лицензия — запросите её у автора (matsuridayo) или исключите каталог.
