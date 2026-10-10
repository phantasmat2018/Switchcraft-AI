from pathlib import Path
p=Path('src/MainWindow.xaml'); t=p.read_text(encoding='utf-8-sig')
t=t.replace('<TextBlock Text="●  Працює локально"','<TextBlock x:Name="PrivacyTitle" Text="●  Працює локально"').replace('<TextBlock Text="Ваш текст залишається&#x0a;на вашому комп’ютері."','<TextBlock x:Name="PrivacyDescription" Text="Ваш текст залишається&#x0a;на вашому комп’ютері."')
card='''<Border Style="{StaticResource Card}" Margin="0,0,0,18"><StackPanel>
 <DockPanel LastChildFill="True"><CheckBox x:Name="TypeSafeToggle" DockPanel.Dock="Right" Style="{StaticResource Toggle}" VerticalAlignment="Center" Click="AiMode_Click" AutomationProperties.Name="Режим TypeSafe AI"/><StackPanel Margin="0,0,20,0"><TextBlock Text="TypeSafe AI" FontSize="18" FontWeight="SemiBold"/><TextBlock Text="Визначати мову через нейромережу" Style="{StaticResource MutedText}" Margin="0,6,0,0"/></StackPanel></DockPanel>
 <TextBlock x:Name="AiModeDescription" Style="{StaticResource MutedText}" Margin="0,16,0,0"/>
 <StackPanel x:Name="AiOptions" Visibility="Collapsed">
  <Border Background="#202E29" BorderBrush="#385044" BorderThickness="1" CornerRadius="8" Padding="14" Margin="0,16,0,18"><TextBlock Text="У цьому режимі поточне слово та до 6 попередніх слів надсилаються api.typesafe.ai. Захищені поля й програми-винятки пропускаються. Запізнілі відповіді не змінюють уже набраний текст." Style="{StaticResource MutedText}" FontSize="12"/></Border>
  <TextBlock Text="Ключ API" FontWeight="SemiBold" Margin="0,0,0,8"/><PasswordBox x:Name="TypeSafeKey" MaxLength="4096" Background="#11141A" Foreground="{StaticResource Text}" BorderBrush="#363D49" Padding="13,10" FontSize="14" AutomationProperties.Name="Ключ API TypeSafe"/>
  <TextBlock x:Name="ApiKeyHint" Style="{StaticResource MutedText}" FontSize="11" Margin="0,7,0,14"/>
  <TextBlock Text="Модель" FontWeight="SemiBold" Margin="0,0,0,8"/><TextBox x:Name="TypeSafeModelBox" Text="jev-latest" MaxLength="80" AutomationProperties.Name="Модель TypeSafe"/>
  <WrapPanel Margin="0,16,0,0"><Button Content="Зберегти підключення" Style="{StaticResource Primary}" Click="SaveAi_Click" Margin="0,0,10,10"/><Button x:Name="TestAiButton" Content="Перевірити API" Click="TestAi_Click" Margin="0,0,10,10"/><Button Content="Прибрати ключ" Click="ClearAiKey_Click" Margin="0,0,0,10"/></WrapPanel>
  <TextBlock x:Name="AiConfigStatus" Style="{StaticResource MutedText}" FontSize="12"/>
  <TextBlock x:Name="AiServiceStatus" Foreground="{StaticResource Accent}" FontSize="12" Margin="0,8,0,0"/>
  <TextBlock Text="Перевірка API надсилає лише тестове слово «ghbdsn». Ключ захищено для вашого облікового запису Windows. Під час швидкого набору нейромережа може пропускати слова; без з’єднання працює локальний рушій." Style="{StaticResource MutedText}" FontSize="11" Margin="0,14,0,0"/>
 </StackPanel>
</StackPanel></Border>'''
needle='<TextBlock Text="Трохи налаштувань, щоб усе працювало по-вашому." Style="{StaticResource MutedText}" Margin="0,0,0,25"/>'
assert needle in t
t=t.replace(needle,needle+card)
t=t.replace('Без облікового запису, мережевих запитів і запису набраного тексту у файли. Історія існує лише в пам’яті й зникає після виходу.','Локальний режим не надсилає текст у мережу. Режим TypeSafe AI використовує ваш ключ API для визначення мови. Історія існує лише в пам’яті й зникає після виходу.')
t=t.replace('Вставте текст із неправильною розкладкою. Регістр і переноси рядків збережуться.','Ручне перетворення працює локально. TypeSafe використовується під час набору та живої перевірки.')
t=t.replace('1.1.2','1.2.0'); p.write_text(t,encoding='utf-8-sig')
p=Path('src/Switchcraft.csproj'); p.write_text(p.read_text(encoding='utf-8-sig').replace('1.1.2','1.2.0'),encoding='utf-8-sig')
