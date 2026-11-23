# macOS-версия

Проект `ChelbasketMac` — отдельный интерфейс на Avalonia. Он использует общие файлы поиска матчей, формирования поста и сборки пакета из `windows-app/ChelbasketDesktop`.

Целевая платформа — .NET 8, чтобы приложение запускалось на macOS 12 Monterey (включая 12.7.6) и новее. .NET runtime уже входит в сборку.

## Скачать приложение

Готовые `.app` для Apple Silicon и Intel публикуются как артефакты workflow **macOS app**: https://github.com/shieFak/CRBF_post/actions/workflows/macos-app.yml

GitHub Actions не только публикует приложение, но и упаковывает его в macOS `.app` bundle. Именно этот артефакт следует передавать коллегам.
