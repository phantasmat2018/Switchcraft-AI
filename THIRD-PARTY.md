# Сторонні компоненти

У складі Switchcraft AI використовуються наступні сторонні бібліотеки, компоненти та моделі:

### 1. Моделі та компоненти штучного інтелекту
- **Laya 0.4.1 / mmBERT-base**: локальна нейромережева модель для класифікації та перемикання розкладки на базі трансформерної архітектури.
  - Енкодер: `jhu-clsp/mmBERT-base` (ліцензія MIT).
  - Модель та адаптер: Laya / ConvAI Innovations (ліцензія Apache 2.0).
  - Дані для навчання: Tatoeba (ліцензія CC BY 2.0 FR).
  - Завантажується окремо через налаштування програми або компонент інсталятора.
- **TypeSafe AI Protocol**: підтримка хмарного визначення розкладки через офіційний API `https://api.typesafe.ai/v1/systemone`.

### 2. Словники та лінгвістичні компоненти
- **WeCantSpell.Hunspell 7.0.1**: бібліотека для швидкої перевірки та валідації словоформ (ліцензія MPL 1.1).
- **Український словник (`uk_UA.dic`, `uk_UA.aff`)**: проєкт dict_uk (Andriy Rysin та спільнота, ліцензія MPL 1.1).
- **Словник російської мови (`ru_RU.dic`, `ru_RU.aff`)**: проєкт Alexander I. Lebedev та спільнота (ліцензія MPL 1.1).
- Ліцензії та повідомлення авторів словників зберігаються у відкритому вигляді.

### 3. Системні компоненти виконання (Runtime)
- **Microsoft .NET 8 / Windows Desktop Runtime**: каркас виконання програми (ліцензія MIT).
- **Microsoft Visual C++ Runtime**: `vcruntime140*.dll`, `msvcp140*.dll`, `concrt140.dll` (ліцензія Microsoft Software License).
- **Swift Core Runtime for Windows**: `swiftCore.dll`, `Foundation*.dll`, `BlocksRuntime.dll`, `dispatch.dll` (ліцензія Apache 2.0 з LLVM Exception).

### 4. Інсталятор
- **NSIS (Nullsoft Scriptable Install System) 3.10**: система створення інсталятора для Windows (ліцензія zlib/libpng).

Усі авторські права на згадані компоненти належать їх законним правовласникам.
