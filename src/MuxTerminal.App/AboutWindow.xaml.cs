using System.Diagnostics;
using System.Windows;
using MuxTerminal.App.Services;

namespace MuxTerminal.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ProductText.Text = AppInfo.Product;
        VersionText.Text = "Версия " + AppInfo.Version;
        DescriptionText.Text = AppInfo.Description;
        CopyrightText.Text = AppInfo.Copyright;
        RepoText.Text = AppInfo.RepositoryUrl;
        SystemText.Text = $"{AppInfo.WindowsVersion} · {AppInfo.FrameworkVersion}";

        var mono = MonoFonts.Get(null);
        LicenseBox.FontFamily = mono;
        NoticesBox.FontFamily = mono;
        LicenseBox.Text = AppInfo.ReadResource("LICENSE");
        NoticesBox.Text = AppInfo.ReadResource("THIRD-PARTY-NOTICES.txt");
        FeaturesBox.Text =
            "Мультиплексор GSM 07.10 / 3GPP TS 27.010 (Basic Option) на одном COM-порту без драйверов: " +
            "каждый логический канал (DLC) — в своей вкладке.\r\n\r\n" +
            "Порядок работы\r\n" +
            "  «Открыть порт» — обычный AT-терминал на вкладке «COM-порт»; «Старт MUX» — перевести модем в MUX\r\n" +
            "  и открыть каналы; «Стоп MUX» — обратно в AT-режим; «Закрыть порт» — закрыть всё.\r\n\r\n" +
            "Окна\r\n" +
            "  - Перетащите вкладку за заголовок: к краю — разделить окно, за пределы — отдельное окно.\r\n" +
            "  - Кнопки «В окно», «Во вкладку», «Справа», «Снизу» на панели каждой вкладки; ПКМ по заголовку — меню.\r\n" +
            "  - Меню «Окна»: собрать все окна во вкладки, готовые раскладки.\r\n\r\n" +
            "Клавиши\r\n" +
            "  Enter — отправить; стрелки вверх/вниз — история команд\r\n" +
            "  F2 — переименовать вкладку канала\r\n" +
            "  Ctrl+Z — отправить текст и 0x1A (завершение SMS)\r\n" +
            "  Ctrl+F — поиск по выводу\r\n" +
            "  Ctrl+Shift+S — настройки отображения и подсветки\r\n" +
            "  Ctrl+Shift+O / Ctrl+Shift+T — вынести вкладку в окно / вернуть во вкладку\r\n\r\n" +
            "Подсветка и «Копилка»\r\n" +
            "  Правила поиска подсвечивают совпадения во всех вкладках и собирают важные строки " +
            "(ошибки, звонки, новые SMS…) во вкладку «Копилка».\r\n\r\n" +
            "Быстрые команды\r\n" +
            "  Кнопки над строкой ввода; свои последовательности — в «Настройки», вкладка «Быстрые команды».\r\n\r\n" +
            "Журналы\r\n" +
            "  Каждый сеанс записывается в файл (меню «Файл» → «Открыть папку журналов»).\r\n\r\n" +
            "Эмулятор модема\r\n" +
            "  Пункт «Эмулятор модема» в списке портов — проверка программы без оборудования.";
    }

    private void RepoLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.RepositoryUrl) { UseShellExecute = true });
        }
        catch
        {
            // Нет браузера по умолчанию — адрес виден в окне.
        }
    }

    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppInfo.Diagnostics);
        }
        catch
        {
            // Буфер обмена занят.
        }
    }
}
