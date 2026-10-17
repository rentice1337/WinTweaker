# Как помочь проекту

Спасибо за интерес к WinTweak! Ошибки, идеи и исправления принимаются через [issues](https://github.com/rentice1337/WinTweaker/issues/new/choose) и pull request.

## Сборка

Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) и Windows.

```
dotnet publish WinTweaker.csproj -c Release -o publish
```

Или запустите `build.bat`. Каждый push и pull request собирается в GitHub Actions — готовый exe лежит в артефактах сборки.

## Как добавить настройку

Откройте `Catalog.cs` (или `CatalogMore.cs`) и допишите строку в нужной категории:

```csharp
t.Add(R("Интерфейс", "Название", "Описание",
    @"HKCU\путь\к\разделу", "ИмяПараметра", значениеВкл, значениеВыкл));
```

- `R` — один параметр реестра, `M` — несколько сразу, `S` — служба, `A` — удаление UWP-приложения.
- `null` вместо значения «выкл» удаляет параметр (возврат к умолчанию Windows).
- Для сложных случаев — `new Tweak { IsApplied = ..., Apply = ..., Revert = ... }`.

Каждая настройка должна:
- иметь понятное описание на русском — что изменится для пользователя;
- полностью откатываться;
- не трогать файлы игр и не внедряться в чужие процессы.

## Как устроен код

| Файл | Что внутри |
|---|---|
| `Core.cs` | модель `Tweak`, работа с реестром и командами |
| `Catalog.cs`, `CatalogMore.cs` | все настройки |
| `MainForm.cs`, `TrayApi.cs`, `Onboarding.cs` | окно, разделы, применение, первый запуск |
| `UI.cs`, `Glass.cs`, `Dialogs.cs` | тема, собственные контролы, стеклянный фон, диалоги |
| `Journal.cs` | журнал изменений и откат |
| `Watch.cs` | сторож настроек |
| `Advisor.cs`, `Dashboard.cs` | оценка системы, советы, главная |
| `Diagnostics.cs`, `Features.cs` | диагностика, диски, мониторы, тест мыши, статистика |
| `Startup.cs`, `Programs.cs` | автозагрузка, установка программ через winget |
| `RussiaNetwork.cs` | вкладка сети с DNS и проверкой доступности сайтов |
| `GameData.cs`, `GameExe.cs` | пресеты игр и настройки Windows для exe игры |
| `GameLibrary.cs`, `EaInstalls.cs` | поиск игр и обложек |
| `GamePrefs.cs`, `GameEdit.cs` | правки списка игр, игры на кольце пульта |
| `GameMode.cs`, `GameNet.cs`, `FpsMeter.cs` | игровой режим, пинг и трассировка, счётчик FPS (ETW) |
| `GamesPage.cs`, `GameWidgets.cs` | страница «Игровой центр» |
| `Tray.cs`, `Wheel.cs`, `WheelSettings.cs` | значок в трее, круговой пульт |
| `Palette.cs` | палитра команд Ctrl+K |

## Оформление изменений

- Один pull request — одна задача.
- Опишите, что изменилось для пользователя, и добавьте строку в `CHANGELOG.md`.
- Проверьте, что сборка в Actions зелёная.
