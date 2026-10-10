# Сторонні компоненти

Switchcraft використовує мовне ядро та набір правил з локальної копії TypeSwitcher 1.1.31, наданої для цієї роботи:

- `TypeSwitcherCABI.dll`, `typeswitcher_rules.json`: TypeSwitcher, DSOFT Digital.
- `swift*.dll`, `Foundation*.dll`, `_FoundationICU.dll`, `BlocksRuntime.dll`, `dispatch.dll`: залежності мовного ядра, скопійовані з того самого локального комплекту без модифікації.
- `msvcp*.dll`, `vcruntime*.dll`, `vccorlib140.dll`, `concrt140.dll`: супровідні компоненти Microsoft із наданого комплекту.
- Автономна збірка також містить Microsoft .NET 8 / Windows Desktop Runtime; його повідомлення про ліцензії постачаються разом із runtime.
- Інсталятор додатково включає теку `Licenses` з ліцензіями Microsoft .NET Runtime / Windows Desktop Runtime 8.0.26 та повідомленнями сторонніх компонентів із відповідних офіційних NuGet-пакетів.
- `WeCantSpell.Hunspell` 7.0.1: бібліотека перевірки словоформ, використовується без змін за опцією MPL 1.1. Джерело та повідомлення авторів: `Dictionaries/NOTICE.md`, `Dictionaries/WeCantSpell.Hunspell-LICENSE.txt`.
- `Dictionaries/uk_UA.dic`, `uk_UA.aff`: український словник dict_uk, Andriy Rysin та учасники проєкту, з репозиторію LibreOffice/dictionaries, MPL 1.1.
- `Dictionaries/ru_RU.dic`, `ru_RU.aff`: російський словник Alexander I. Lebedev та учасників, з того самого репозиторію. Повні умови та авторські повідомлення — `Dictionaries/README_ru_RU.txt`.

Файли словників постачаються у вихідному форматі без змін. Точні ревізії, джерела й текст MPL 1.1 містяться в теці `Dictionaries` поруч із виконуваним файлом.

Необов’язкове середовище Laya завантажується окремо з налаштувань і не входить до виконуваного файлу. Модель r3 містить `NOTICE.txt`: енкодер jhu-clsp/mmBERT-base (MIT), Laya 0.4.1 / ConvAI Innovations (Apache 2.0) та навчальні речення Tatoeba (CC BY 2.0 FR). Python, uv, PyTorch і залежності зберігають власні ліцензії й повідомлення у встановлених пакетах. Адаптер Switchcraft не змінює ваги або авторські повідомлення моделі.

Оригінальні авторські права цих компонентів зберігаються. Цей проєкт не оголошує стороннє ядро власним кодом та не встановлює для нього нову ліцензію. Нові вихідні тексти оболонки розміщені в `src/`. Файли оригінального деінсталятора, виконуваний TypeSwitcher.exe та WinSparkle не входять у збірку Switchcraft.
