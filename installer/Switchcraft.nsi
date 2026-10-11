Unicode true
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
SetDatablockOptimize on

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "WordFunc.nsh"
!include "FileFunc.nsh"
!include "Sections.nsh"

!ifndef PRODUCT_VERSION
  !error "Use tools/build-installer.ps1 to supply the verified payload."
!endif
!define APP_ID "SwitchcraftAI.Desktop.A1C3E5F7-2B4D-6E8F-0A1C-3E5F7A1C3E5F"
!ifdef INSTALLER_TEST
  !define REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SwitchcraftAI.InstallerTest"
  !define SHORTCUT_DIR "$INSTDIR\_test-shortcuts"
  !define DESKTOP_LINK "$INSTDIR\_test-desktop\Switchcraft AI.lnk"
  !define SETUP_MUTEX "Local\SwitchcraftAI.InstallerTest.Setup"
!else
  !define REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Switchcraft-AI"
  !define SHORTCUT_DIR "$SMPROGRAMS\Switchcraft AI"
  !define DESKTOP_LINK "$DESKTOP\Switchcraft AI.lnk"
  !define SETUP_MUTEX "Local\SwitchcraftAI.Setup"
!endif

Name "Switchcraft AI ${PRODUCT_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Switchcraft-AI"
BrandingText "Switchcraft AI"
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1058 "ProductName" "Switchcraft AI"
VIAddVersionKey /LANG=1058 "FileDescription" "Інсталятор Switchcraft AI"
VIAddVersionKey /LANG=1058 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1058 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1058 "LegalCopyright" "Switchcraft AI; права на сторонні компоненти належать їхнім авторам."

!define MUI_ICON "${PROJECT_ROOT}\src\Assets\Switchcraft.ico"
!define MUI_UNICON "${PROJECT_ROOT}\src\Assets\Switchcraft.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Встановлення Switchcraft AI"
!define MUI_WELCOMEPAGE_TEXT "Автоматичне виправлення розкладок за допомогою TypeSafe AI та локальної моделі Laya.$\r$\n$\r$\nПрограма встановлюється для поточного користувача. Необов'язкову локальну модель Laya можна підготувати одразу після встановлення або пізніше в налаштуваннях.$\r$\n$\r$\nПеред продовженням завершіть Switchcraft AI через меню значка в треї."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "Switchcraft AI встановлено"
!define MUI_FINISHPAGE_TEXT "Програму можна відкрити через меню «Пуск». Налаштування доступні через значок Switchcraft AI у треї.$\r$\n$\r$\nЯкщо вибрано Laya, після натискання «Готово» Switchcraft AI відкриє налаштування й підготує модель. Це можна скасувати й продовжити пізніше."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp
!define MUI_FINISHPAGE_RUN_TEXT "Запустити Switchcraft AI"
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "Буде видалено файли Switchcraft AI та створені інсталятором ярлики. Особисті налаштування й правила залишаться збереженими."
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Ukrainian"

Var SetupMutex
Var PreviousInstallDir
Var LayaLaunched

!macro RunningCheck PREFIX
Function ${PREFIX}RequireAppClosed
  retry:
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\Switchcraft.Desktop") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    IfSilent blocked
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Switchcraft AI ще працює. Виберіть «Вийти» в меню значка в треї, потім натисніть «Повторити»." IDRETRY retry
    blocked:
    SetErrorLevel 1618
    Quit
  ${EndIf}
FunctionEnd
!macroend
!insertmacro RunningCheck ""
!insertmacro RunningCheck "un."

Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Switchcraft AI потребує 64-бітної Windows." /SD IDOK
    SetErrorLevel 1633
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Switchcraft AI потребує Windows 10 або новішої версії." /SD IDOK
    SetErrorLevel 1633
    Quit
  ${EndIf}
  SetRegView 64
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "${SETUP_MUTEX}") p.r0'
  StrCpy $SetupMutex $0
  System::Call 'kernel32::GetLastError() i.r1'
  ${If} $1 == 183
    MessageBox MB_OK|MB_ICONEXCLAMATION "Інший інсталятор Switchcraft AI уже працює." /SD IDOK
    SetErrorLevel 1618
    Quit
  ${EndIf}
  ReadRegStr $PreviousInstallDir HKCU "${REG_KEY}" "InstallLocation"
  ReadRegStr $0 HKCU "${REG_KEY}" "DisplayVersion"
  ${If} $0 != ""
    ${VersionCompare} "$0" "${PRODUCT_VERSION}" $1
    ${If} $1 == 1
      MessageBox MB_OK|MB_ICONSTOP "Уже встановлено Switchcraft AI $0. Цей інсталятор містить версію ${PRODUCT_VERSION} і не замінює новішу версію старішою." /SD IDOK
      SetErrorLevel 1638
      Quit
    ${EndIf}
  ${EndIf}
  ${If} $PreviousInstallDir != ""
  ${AndIf} $INSTDIR == "$LOCALAPPDATA\Programs\Switchcraft-AI"
    StrCpy $INSTDIR $PreviousInstallDir
  ${EndIf}
  Call InitLayaSection
  Call RequireAppClosed
FunctionEnd

Section "Switchcraft AI (основні компоненти)" SecCore
  SectionIn RO
  Call RequireAppClosed
  SetOutPath "$INSTDIR"
  SetOverwrite on
  !include "${INSTALL_FILES}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteINIStr "$INSTDIR\switchcraft-install.ini" "Install" "Id" "${APP_ID}"
  WriteINIStr "$INSTDIR\switchcraft-install.ini" "Install" "Directory" "$INSTDIR"
  WriteINIStr "$INSTDIR\switchcraft-install.ini" "Install" "Version" "${PRODUCT_VERSION}"
  DeleteINIStr "$INSTDIR\switchcraft-install.ini" "Install" "LayaRequested"
  CreateDirectory "${SHORTCUT_DIR}"
  CreateShortcut "${SHORTCUT_DIR}\Switchcraft AI.lnk" "$INSTDIR\Switchcraft.exe" "" "$INSTDIR\Switchcraft.exe" 0
  CreateShortcut "${SHORTCUT_DIR}\Видалити Switchcraft AI.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${REG_KEY}" "DisplayName" "Switchcraft AI"
  WriteRegStr HKCU "${REG_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${REG_KEY}" "Publisher" "Switchcraft AI"
  WriteRegStr HKCU "${REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REG_KEY}" "DisplayIcon" "$INSTDIR\Switchcraft.exe,0"
  WriteRegStr HKCU "${REG_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${REG_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${REG_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REG_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${REG_KEY}" "EstimatedSize" ${INSTALLED_KB}
  !ifndef INSTALLER_TEST
    ; Preserve the user's existing autostart choice while updating its path.
    ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "SwitchcraftAI.Keyboard"
    ${If} $0 != ""
      WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "SwitchcraftAI.Keyboard" '$\"$INSTDIR\Switchcraft.exe$\" --minimized'
    ${EndIf}
  !endif
SectionEnd

Section "Ярлик на робочому столі" SecDesktop
  !ifdef INSTALLER_TEST
    CreateDirectory "$INSTDIR\_test-desktop"
  !endif
  CreateShortcut "${DESKTOP_LINK}" "$INSTDIR\Switchcraft.exe" "" "$INSTDIR\Switchcraft.exe" 0
  WriteINIStr "$INSTDIR\switchcraft-install.ini" "Install" "DesktopShortcut" "1"
SectionEnd

; Switchcraft itself downloads and verifies Laya after installation, with progress and cancel in its settings.
Section "Локальна модель Laya" SecLaya
  AddSize 2000000
  WriteINIStr "$INSTDIR\switchcraft-install.ini" "Install" "LayaRequested" "1"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCore} "Програма, механізм правил та необхідне середовище виконання."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Створити ярлик Switchcraft AI на робочому столі."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecLaya} "Після встановлення завантажити з GitHub модель Laya r3 (≈600 МБ) і компоненти (≈1 ГБ, для NVIDIA CUDA — більше), перевірити контрольні суми й підготувати для поточного користувача. Потрібні інтернет і до 5 ГБ вільного місця."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function InitLayaSection
  ${If} ${FileExists} "$LOCALAPPDATA\Switchcraft\Laya\model\rl_agent_config.json"
  ${AndIf} ${FileExists} "$LOCALAPPDATA\Switchcraft\Laya\venv\Scripts\python.exe"
    SectionSetText ${SecLaya} "Локальна модель Laya (вже підготовлена)"
    !insertmacro UnselectSection ${SecLaya}
    !insertmacro SetSectionFlag ${SecLaya} ${SF_RO}
    Return
  ${EndIf}
  ; Switchcraft AI shares the Laya folder with Switchcraft (LayaSetup.DefaultRoot).
  ; Silent installs prepare Laya only on request: Setup.exe /S /LAYA
  ${If} ${Silent}
    ${GetParameters} $0
    ClearErrors
    ${GetOptions} $0 "/LAYA" $1
    ${If} ${Errors}
      !insertmacro UnselectSection ${SecLaya}
    ${EndIf}
  ${EndIf}
FunctionEnd

Function LaunchApp
  ${If} ${SectionIsSelected} ${SecLaya}
    StrCpy $LayaLaunched 1
    !ifndef INSTALLER_TEST
      Exec '"$INSTDIR\Switchcraft.exe" --setup-laya'
    !endif
  ${Else}
    Exec '"$INSTDIR\Switchcraft.exe"'
  ${EndIf}
FunctionEnd

Function .onInstSuccess
  ; The Laya choice is honoured even when "Run" was unchecked or the install was silent.
  !ifndef INSTALLER_TEST
    ${If} ${SectionIsSelected} ${SecLaya}
    ${AndIf} $LayaLaunched != 1
      Exec '"$INSTDIR\Switchcraft.exe" --setup-laya'
    ${EndIf}
  !endif
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ReadINIStr $0 "$INSTDIR\switchcraft-install.ini" "Install" "Id"
  ReadINIStr $1 "$INSTDIR\switchcraft-install.ini" "Install" "Directory"
  ${If} $0 != "${APP_ID}"
  ${OrIf} $1 != "$INSTDIR"
    MessageBox MB_OK|MB_ICONSTOP "Не вдалося перевірити теку встановлення Switchcraft AI. Видалення скасовано." /SD IDOK
    SetErrorLevel 1603
    Quit
  ${EndIf}
  Call un.RequireAppClosed
FunctionEnd

Section "Uninstall"
  Call un.RequireAppClosed
  ReadINIStr $0 "$INSTDIR\switchcraft-install.ini" "Install" "DesktopShortcut"
  ${If} $0 == "1"
    Delete "${DESKTOP_LINK}"
  ${EndIf}
  Delete "${SHORTCUT_DIR}\Switchcraft AI.lnk"
  Delete "${SHORTCUT_DIR}\Видалити Switchcraft AI.lnk"
  RMDir "${SHORTCUT_DIR}"
  !ifdef INSTALLER_TEST
    RMDir "$INSTDIR\_test-desktop"
  !else
    ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "SwitchcraftAI.Keyboard"
    ${If} $0 == '$\"$INSTDIR\Switchcraft.exe$\" --minimized'
      DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "SwitchcraftAI.Keyboard"
    ${EndIf}
  !endif
  ; Delete only manifest-listed application files, never arbitrary user content.
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\switchcraft-install.ini"
  Delete "$INSTDIR\Uninstall.exe"
  ReadRegStr $0 HKCU "${REG_KEY}" "InstallLocation"
  ${If} $0 == "$INSTDIR"
    DeleteRegKey HKCU "${REG_KEY}"
  ${EndIf}
  SetOutPath "$TEMP"
  RMDir "$INSTDIR"
SectionEnd
