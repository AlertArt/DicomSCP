/**
 * DICOM 管理系统 - 轻量多语言支持（中文 / English / Polski）
 *
 * 设计说明：
 * - 采用"源文本内容匹配"翻译：以简体中文文案为键，切换语言时遍历
 *   DOM 文本节点与 placeholder/title/aria-label 属性做精确替换。
 *   业务 HTML 与 JS 无需包裹任何翻译标记，新增语言只需在 STRINGS
 *   中补一个字段。
 * - 支持带占位符的模板词条（键中含 {0}、{1} 等），用于翻译"插值
 *   后"的动态文本，例如"已添加 12 个DICOM文件"会匹配键
 *   "已添加 {0} 个DICOM文件"。捕获到的片段若仍是中文会再次翻译。
 * - MutationObserver 监听动态注入的节点（表格行、Toast 等），
 *   并记录每个节点/属性的中文原文，切回中文时自动还原。
 *   翻译结果本身不再命中中文键，因此无循环风险。
 * - 语言偏好存于 localStorage（键 dicom-lang），首次访问按浏览器
 *   语言自动选择（pl* → pl，en* → en，其余 → zh）。
 * - 覆盖范围：四个页面的全部静态文案与高频动态文案（状态、操作、
 *   提示、Toasts、弹窗标题等），含带变量插值的动态语句。
 *
 * 注意：English/Polski 译文为初版，波兰语文案建议由母语者复核。
 */
window.I18N = (function () {
    'use strict';

    var STORAGE_KEY = 'dicom-lang';
    var SUPPORTED = ['zh', 'en', 'pl'];
    var currentLang = 'zh';
    /* 页面标题的中文原文，切回中文时还原 */
    var originalTitle = document.title;

    /* 键 = 简体中文源文案（与页面文本一致），值 = 各语言译文 */
    var STRINGS = {
        /* ===== 通用操作 ===== */
        '保存': { en: 'Save', pl: 'Zapisz' },
        '保存配置': { en: 'Save Config', pl: 'Zapisz konfigurację' },
        '确认保存': { en: 'Confirm Save', pl: 'Potwierdź zapis' },
        '正在保存配置...': { en: 'Saving config...', pl: 'Zapisywanie konfiguracji...' },
        '配置保存成功！需要手动重启服务生效！': { en: 'Config saved! Restart the service manually to apply!', pl: 'Konfiguracja zapisana! Aby zastosować, ręcznie zrestartuj usługę!' },
        '保存配置失败': { en: 'Failed to save config', pl: 'Nie udało się zapisać konfiguracji' },
        '配置格式不正确，请检查JSON格式': { en: 'Invalid config format, please check the JSON', pl: 'Nieprawidłowy format konfiguracji, sprawdź JSON' },
        '查询': { en: 'Search', pl: 'Szukaj' },
        '重置': { en: 'Reset', pl: 'Resetuj' },
        '取消': { en: 'Cancel', pl: 'Anuluj' },
        '确认修改': { en: 'Confirm Change', pl: 'Potwierdź zmianę' },
        '确认删除': { en: 'Confirm Deletion', pl: 'Potwierdź usunięcie' },
        '确认获取': { en: 'Confirm Retrieve', pl: 'Potwierdź pobranie' },
        '刷新': { en: 'Refresh', pl: 'Odśwież' },
        '测试': { en: 'Test', pl: 'Testuj' },
        '查看': { en: 'View', pl: 'Podgląd' },
        '编辑': { en: 'Edit', pl: 'Edytuj' },
        '删除': { en: 'Delete', pl: 'Usuń' },
        '发送': { en: 'Send', pl: 'Wyślij' },
        '发送文件': { en: 'Send Files', pl: 'Wyślij pliki' },
        '预览': { en: 'Preview', pl: 'Podgląd' },
        '详情': { en: 'Details', pl: 'Szczegóły' },
        '下载': { en: 'Download', pl: 'Pobierz' },
        '调度': { en: 'Schedule', pl: 'Zaplanuj' },
        '预约': { en: 'Book', pl: 'Zarezerwuj' },
        '添加预约': { en: 'Add Booking', pl: 'Dodaj rezerwację' },
        '提示': { en: 'Notice', pl: 'Uwaga' },
        '其他': { en: 'Other', pl: 'Inne' },
        '全部': { en: 'All', pl: 'Wszystkie' },
        '全部状态': { en: 'All Statuses', pl: 'Wszystkie statusy' },
        '类型': { en: 'Type', pl: 'Typ' },
        '状态': { en: 'Status', pl: 'Status' },
        '日期': { en: 'Date', pl: 'Data' },
        '时间': { en: 'Time', pl: 'Czas' },
        '大小': { en: 'Size', pl: 'Rozmiar' },
        '操作': { en: 'Actions', pl: 'Akcje' },
        '未知': { en: 'Unknown', pl: 'Nieznany' },
        '失败': { en: 'Failed', pl: 'Niepowodzenie' },
        '加载中...': { en: 'Loading...', pl: 'Wczytywanie...' },
        '清空列表': { en: 'Clear List', pl: 'Wyczyść listę' },
        '操作成功': { en: 'Operation successful', pl: 'Operacja zakończona' },
        '删除成功': { en: 'Deleted successfully', pl: 'Usunięto pomyślnie' },
        '删除失败': { en: 'Failed to delete', pl: 'Nie udało się usunąć' },
        '保存失败': { en: 'Failed to save', pl: 'Nie udało się zapisać' },
        '获取失败': { en: 'Failed to fetch', pl: 'Nie udało się pobrać' },
        '获取失败，请检查网络连接': { en: 'Failed to fetch, please check the network', pl: 'Nie udało się pobrać, sprawdź połączenie sieciowe' },
        '暂无数据': { en: 'No data', pl: 'Brak danych' },
        '加载失败，请重试': { en: 'Loading failed, please retry', pl: 'Wczytywanie nie powiodło się, spróbuj ponownie' },
        '初始化失败': { en: 'Initialization failed', pl: 'Inicjalizacja nie powiodła się' },
        '更新分页信息失败': { en: 'Failed to update pagination', pl: 'Nie udało się zaktualizować paginacji' },

        /* ===== 登录 / 用户 ===== */
        '登录 - DICOM 管理系统': { en: 'Sign in - DICOM Management', pl: 'Logowanie - Zarządzanie DICOM' },
        'DICOM 管理系统': { en: 'DICOM Management', pl: 'Zarządzanie DICOM' },
        'DICOM管理系统': { en: 'DICOM Management', pl: 'Zarządzanie DICOM' },
        'DICOM管理系统 by': { en: 'DICOM Management by', pl: 'Zarządzanie DICOM by' },
        '登录系统': { en: 'Sign In', pl: 'Zaloguj się' },
        '用户名': { en: 'Username', pl: 'Nazwa użytkownika' },
        '密码': { en: 'Password', pl: 'Hasło' },
        '请输入用户名': { en: 'Enter username', pl: 'Wpisz nazwę użytkownika' },
        '请输入密码': { en: 'Enter password', pl: 'Wpisz hasło' },
        '退出系统': { en: 'Sign Out', pl: 'Wyloguj się' },
        '修改密码': { en: 'Change Password', pl: 'Zmiana hasła' },
        '当前密码': { en: 'Current Password', pl: 'Aktualne hasło' },
        '新密码': { en: 'New Password', pl: 'Nowe hasło' },
        '确认新密码': { en: 'Confirm New Password', pl: 'Powtórz nowe hasło' },
        '请输入当前密码': { en: 'Enter current password', pl: 'Wpisz aktualne hasło' },
        '请输入新密码': { en: 'Enter new password', pl: 'Wpisz nowe hasło' },
        '请再次输入新密码': { en: 'Re-enter new password', pl: 'Powtórz nowe hasło' },
        '两次输入的新密码不一致': { en: 'The two new passwords do not match', pl: 'Nowe hasła nie są identyczne' },
        '新密码长度不能少于6位': { en: 'New password must be at least 6 characters', pl: 'Nowe hasło musi mieć co najmniej 6 znaków' },
        '新密码不能与旧密码相同': { en: 'New password must differ from the old one', pl: 'Nowe hasło musi różnić się od starego' },
        '密码修改成功，请重新登录': { en: 'Password changed, please sign in again', pl: 'Hasło zmienione, zaloguj się ponownie' },
        '修改密码失败，请重试': { en: 'Failed to change password, please retry', pl: 'Nie udało się zmienić hasła, spróbuj ponownie' },
        '获取用户信息失败:': { en: 'Failed to get user info:', pl: 'Nie udało się pobrać informacji o użytkowniku:' },
        '未知用户': { en: 'Unknown user', pl: 'Nieznany użytkownik' },

        /* ===== 导航 / 页面 ===== */
        '预约登记': { en: 'Worklist', pl: 'Rejestracja' },
        '影像管理': { en: 'Image Management', pl: 'Zarządzanie obrazami' },
        '查询检索': { en: 'Query/Retrieve', pl: 'Zapytania/Pobieranie' },
        '发送图像': { en: 'Send Images', pl: 'Wysyłanie obrazów' },
        '打印管理': { en: 'Print Management', pl: 'Zarządzanie drukowaniem' },
        '日志管理': { en: 'Log Management', pl: 'Zarządzanie logami' },
        '系统设置': { en: 'Settings', pl: 'Ustawienia' },
        '系统配置': { en: 'System Config', pl: 'Konfiguracja systemu' },
        '其他配置': { en: 'Other Config', pl: 'Inne ustawienia' },
        '配置说明': { en: 'Config Guide', pl: 'Opis konfiguracji' },
        '参数说明': { en: 'Parameter Guide', pl: 'Opis parametrów' },
        'DICOM 查看器': { en: 'DICOM Viewer', pl: 'Przeglądarka DICOM' },
        '点击查看详细信息': { en: 'Click for details', pl: 'Kliknij, aby zobaczyć szczegóły' },

        /* ===== 检查/患者字段 ===== */
        '患者ID': { en: 'Patient ID', pl: 'ID pacjenta' },
        '病人ID': { en: 'Patient ID', pl: 'ID pacjenta' },
        '姓名': { en: 'Name', pl: 'Imię i nazwisko' },
        '性别': { en: 'Sex', pl: 'Płeć' },
        '男': { en: 'Male', pl: 'Mężczyzna' },
        '女': { en: 'Female', pl: 'Kobieta' },
        '年龄': { en: 'Age', pl: 'Wiek' },
        '检查号': { en: 'Accession No.', pl: 'Numer badania' },
        '检查类型': { en: 'Modality', pl: 'Typ badania' },
        '检查日期': { en: 'Study Date', pl: 'Data badania' },
        '检查时间': { en: 'Study Time', pl: 'Czas badania' },
        '检查描述': { en: 'Study Description', pl: 'Opis badania' },
        '检查备注': { en: 'Study Remark', pl: 'Uwaga do badania' },
        '检查部位': { en: 'Body Part', pl: 'Część ciała' },
        '检查UID': { en: 'Study UID', pl: 'UID badania' },
        '检查中': { en: 'In Progress', pl: 'W trakcie' },
        '关键字': { en: 'Keyword', pl: 'Słowo kluczowe' },
        '预约时间': { en: 'Scheduled Time', pl: 'Czas rezerwacji' },
        '设备AE Title': { en: 'Station AE Title', pl: 'AE stacji' },
        '设备名称': { en: 'Station Name', pl: 'Nazwa stacji' },
        '请求方AE': { en: 'Requesting AE', pl: 'Żądający AE' },
        '创建时间': { en: 'Created At', pl: 'Czas utworzenia' },
        '修改时间': { en: 'Updated At', pl: 'Czas modyfikacji' },
        '序列数': { en: 'Series', pl: 'Serie' },
        '图像数': { en: 'Images', pl: 'Obrazy' },
        '任务ID': { en: 'Job ID', pl: 'ID zadania' },
        '日期范围': { en: 'Date Range', pl: 'Zakres dat' },

        /* ===== 状态 ===== */
        '已预约': { en: 'Scheduled', pl: 'Zarezerwowano' },
        '已完成': { en: 'Completed', pl: 'Zakończono' },
        '已中断': { en: 'Discontinued', pl: 'Przerwano' },
        '已创建': { en: 'Created', pl: 'Utworzono' },
        '已接收': { en: 'Image Received', pl: 'Odebrano obraz' },

        /* ===== 分页 ===== */
        '显示': { en: 'Showing', pl: 'Wyświetlono' },
        '条，共': { en: 'of', pl: 'z' },
        '条': { en: 'entries', pl: 'wpisów' },
        '分页导航': { en: 'Pagination', pl: 'Paginacja' },
        '上一页': { en: 'Previous Page', pl: 'Poprzednia strona' },
        '下一页': { en: 'Next Page', pl: 'Następna strona' },
        '上页': { en: 'Prev', pl: 'Poprzednia' },

        /* ===== 发送图像 ===== */
        '目标节点': { en: 'Destination Node', pl: 'Węzeł docelowy' },
        '选择文件或拖到此处': { en: 'Choose files or drag here', pl: 'Wybierz pliki lub przeciągnij tutaj' },
        '拖拽文件/文件夹到这里，或点击选择': { en: 'Drag files/folders here, or click to browse', pl: 'Przeciągnij pliki/foldery tutaj lub kliknij, aby wybrać' },
        'DICOM文件': { en: 'DICOM Files', pl: 'Pliki DICOM' },
        '选择文件夹': { en: 'Choose Folder', pl: 'Wybierz folder' },
        '已选择的文件：': { en: 'Selected files:', pl: 'Wybrane pliki:' },
        '文件名': { en: 'File Name', pl: 'Nazwa pliku' },
        '清空': { en: 'Clear', pl: 'Wyczyść' },
        'PACS节点': { en: 'PACS Node', pl: 'Węzeł PACS' },
        '未配置DICOM节点': { en: 'No DICOM nodes configured', pl: 'Brak skonfigurowanych węzłów DICOM' },
        '未配置PACS节点': { en: 'No PACS nodes configured', pl: 'Brak skonfigurowanych węzłów PACS' },
        '注意：发送图像和查询检索功能会根据节点的 Type 自动过滤显示对应的节点。': { en: 'Note: Send Images and Query/Retrieve automatically filter nodes by the node Type.', pl: 'Uwaga: Wysyłanie obrazów oraz zapytania/pobieranie automatycznie filtrują węzły według typu.' },

        /* ===== 查看器工具 ===== */
        '窗宽窗位': { en: 'Window/Level', pl: 'Okno/poziom' },
        '缩放': { en: 'Zoom', pl: 'Powiększenie' },
        '平移': { en: 'Pan', pl: 'Przesuwanie' },
        '测距': { en: 'Measure', pl: 'Pomiar odległości' },
        '角度': { en: 'Angle', pl: 'Kąt' },
        '矩形': { en: 'Rectangle', pl: 'Prostokąt' },
        '椭圆': { en: 'Ellipse', pl: 'Elipsa' },
        '探针': { en: 'Probe', pl: 'Sonda' },
        '反相': { en: 'Invert', pl: 'Odwróć' },
        '清除标注': { en: 'Clear Annotations', pl: 'Wyczyść adnotacje' },
        '播放/暂停': { en: 'Play/Pause', pl: 'Odtwórz/Wstrzymaj' },

        /* ===== 日志 ===== */
        '日志配置 (Logging)': { en: 'Log Config (Logging)', pl: 'Konfiguracja logów (Logging)' },
        '各服务的日志配置，包含：': { en: 'Log config per service, containing:', pl: 'Konfiguracja logów usług, zawiera:' },
        '日志级别说明：': { en: 'Log level guide:', pl: 'Opis poziomów logów:' },
        '日志根目录。例如logs': { en: 'Log root directory. E.g. logs', pl: 'Katalog główny logów. Np. logs' },
        '日志保留天数。默认：31': { en: 'Log retention days. Default: 31', pl: 'Dni przechowywania logów. Domyślnie: 31' },
        'LogPath - 日志文件路径': { en: 'LogPath - log file path', pl: 'LogPath - ścieżka plików logów' },
        'OutputTemplate - 输出模板': { en: 'OutputTemplate - output template', pl: 'OutputTemplate - szablon wyjściowy' },
        'MinimumLevel - 最低日志级别：': { en: 'MinimumLevel - minimum log level:', pl: 'MinimumLevel - minimalny poziom logów:' },
        'Verbose - 最详细的日志': { en: 'Verbose - most detailed logs', pl: 'Verbose - najbardziej szczegółowe logi' },
        'Debug - 调试信息': { en: 'Debug - debugging information', pl: 'Debug - informacje debugowania' },
        'Information - 一般信息': { en: 'Information - general information', pl: 'Information - informacje ogólne' },
        'Warning - 警告信息': { en: 'Warning - warning messages', pl: 'Warning - ostrzeżenia' },
        'Error - 错误信息': { en: 'Error - error messages', pl: 'Error - komunikaty błędów' },
        'Fatal - 致命错误': { en: 'Fatal - fatal errors', pl: 'Fatal - błędy krytyczne' },
        'Verbose：最详细的调试信息，包含所有操作细节': { en: 'Verbose: the most detailed debugging information, including all operations', pl: 'Verbose: najbardziej szczegółowe informacje debugowania, ze wszystkimi operacjami' },
        'Debug：调试信息，用于开发和故障排查': { en: 'Debug: debugging information for development and troubleshooting', pl: 'Debug: informacje debugowania dla rozwoju i diagnostyki' },
        'Information：一般信息，记录正常的操作流程': { en: 'Information: general information recording normal operations', pl: 'Information: informacje ogólne o normalnym przebiegu operacji' },
        'Warning：警告信息，表示可能的问题但不影响系统运行': { en: 'Warning: possible issues that do not affect operation', pl: 'Warning: możliwe problemy, które nie wpływają na działanie' },
        'Error：错误信息，表示发生了需要处理的错误': { en: 'Error: errors that need to be handled', pl: 'Error: błędy wymagające obsługi' },
        'Fatal：致命错误，表示系统无法继续运行的严重问题': { en: 'Fatal: critical problems that prevent the system from continuing', pl: 'Fatal: problemy krytyczne uniemożliwiające dalsze działanie' },
        '加载日志内容失败:': { en: 'Failed to load log content:', pl: 'Nie udało się wczytać treści logów:' },
        '刷新日志内容失败:': { en: 'Failed to refresh log content:', pl: 'Nie udało się odświeżyć treści logów:' },
        '更新日志文件列表失败:': { en: 'Failed to update log file list:', pl: 'Nie udało się zaktualizować listy plików logów:' },
        '获取日志内容失败:': { en: 'Failed to get log content:', pl: 'Nie udało się pobrać treści logów:' },
        '删除日志失败:': { en: 'Failed to delete log:', pl: 'Nie udało się usunąć logów:' },
        '绑定日志事件失败:': { en: 'Failed to bind log events:', pl: 'Nie udało się powiązać zdarzeń logów:' },
        '暂无日志内容': { en: 'No log content', pl: 'Brak treści logów' },

        /* ===== 配置说明（STORESCP / 服务） ===== */
        'STORESCP配置 (DicomSettings)': { en: 'STORESCP Config (DicomSettings)', pl: 'Konfiguracja STORESCP (DicomSettings)' },
        'STORESCP高级配置 (Advanced)': { en: 'STORESCP Advanced Config (Advanced)', pl: 'Konfiguracja zaawansowana STORESCP (Advanced)' },
        'Worklist配置 (WorklistSCP)': { en: 'Worklist Config (WorklistSCP)', pl: 'Konfiguracja Worklist (WorklistSCP)' },
        '查询检索服务配置 (QRSCP)': { en: 'Query/Retrieve Service Config (QRSCP)', pl: 'Konfiguracja usługi zapytań/pobierania (QRSCP)' },
        '打印服务配置 (PrintSCP)': { en: 'Print Service Config (PrintSCP)', pl: 'Konfiguracja usługi drukowania (PrintSCP)' },
        '打印客户端配置 (PrintSCU)': { en: 'Print Client Config (PrintSCU)', pl: 'Konfiguracja klienta drukowania (PrintSCU)' },
        'QRSCU/STORESCU配置 (RemoteNodes节点配置)': { en: 'QRSCU/STORESCU Config (RemoteNodes)', pl: 'Konfiguracja QRSCU/STORESCU (RemoteNodes)' },
        'DICOM 应用实体标题，1-16个字符，只能包含字母、数字、下划线和横线。例如：STORESCP': { en: 'DICOM AE Title, 1-16 characters, only letters, digits, underscores and hyphens. E.g.: STORESCP', pl: 'DICOM AE Title, 1-16 znaków, tylko litery, cyfry, podkreślenia i myślniki. Np.: STORESCP' },
        'DICOM文件存储路径。使用正斜杠(/)或双反斜杠(\\\\)，例如：': { en: 'DICOM file storage path. Use forward slashes (/) or double backslashes (\\), e.g.:', pl: 'Ścieżka przechowywania plików DICOM. Użyj ukośników (/) lub podwójnych odwrotnych (\\), np.:' },
        '正斜杠格式：./received_files': { en: 'Forward slash format: ./received_files', pl: 'Format z ukośnikiem: ./received_files' },
        '反斜杠格式：D:\\\\dicom\\\\storage': { en: 'Backslash format: D:\\\\dicom\\\\storage', pl: 'Format z odwrotnym ukośnikiem: D:\\\\dicom\\\\storage' },
        '备注模糊搜索': { en: 'Fuzzy search by remark', pl: 'Wyszukiwanie przybliżone po uwadze' },
        '输入ID': { en: 'Enter ID', pl: 'Wpisz ID' },
        '输入姓名': { en: 'Enter name', pl: 'Wpisz nazwisko' },
        '输入检查号': { en: 'Enter accession no.', pl: 'Wpisz numer badania' },
        '临时文件存储路径。使用正斜杠(/)或双反斜杠(\\\\)，例如：': { en: 'Temp file storage path. Use forward slashes (/) or double backslashes (\\), e.g.:', pl: 'Ścieżka plików tymczasowych. Użyj ukośników (/) lub podwójnych odwrotnych (\\), np.:' },
        '相对路径：./temp_files': { en: 'Relative path: ./temp_files', pl: 'Ścieżka względna: ./temp_files' },
        '绝对路径：D:\\\\dicom\\\\temp': { en: 'Absolute path: D:\\dicom\\temp', pl: 'Ścieżka bezwzględna: D:\\dicom\\temp' },
        '存储服务端口号，范围1-65535。建议使用11112等标准端口': { en: 'Storage service port, range 1-65535. Standard ports such as 11112 are recommended', pl: 'Port usługi przechowywania, zakres 1-65535. Zalecane porty standardowe, np. 11112' },
        'Worklist服务的AE Title。例如：WORKLISTSCP': { en: 'AE Title of the Worklist service. E.g.: WORKLISTSCP', pl: 'AE Title usługi Worklist. Np.: WORKLISTSCP' },
        'Worklist服务端口号，范围1-65535。建议：11113': { en: 'Worklist service port, range 1-65535. Recommended: 11113', pl: 'Port usługi Worklist, zakres 1-65535. Zalecane: 11113' },
        '查询检索服务的AE Title。例如：QRSCP': { en: 'AE Title of the Query/Retrieve service. E.g.: QRSCP', pl: 'AE Title usługi zapytań/pobierania. Np.: QRSCP' },
        '查询检索服务端口号。建议：11114': { en: 'Query/Retrieve service port. Recommended: 11114', pl: 'Port usługi zapytań/pobierania. Zalecane: 11114' },
        '打印服务的AE Title。例如：PRINTSCP': { en: 'AE Title of the Print service. E.g.: PRINTSCP', pl: 'AE Title usługi drukowania. Np.: PRINTSCP' },
        '打印服务端口号。建议：11115': { en: 'Print service port. Recommended: 11115', pl: 'Port usługi drukowania. Zalecane: 11115' },
        '打印客户端的AE Title。例如：PRINTSCU': { en: 'AE Title of the print client. E.g.: PRINTSCU', pl: 'AE Title klienta drukowania. Np.: PRINTSCU' },
        '本地AE Title，STORESCU共用这个AE。例如：QRSCU': { en: 'Local AE Title, shared with STORESCU. E.g.: QRSCU', pl: 'Lokalny AE Title, współdzielony ze STORESCU. Np.: QRSCU' },
        '远程节点配置列表，每个节点包含：': { en: 'Remote node list config, each node contains:', pl: 'Konfiguracja listy węzłów zdalnych, każdy węzeł zawiera:' },
        'Name - 节点名称': { en: 'Name - node name', pl: 'Name - nazwa węzła' },
        'AeTitle - 节点AE Title': { en: 'AeTitle - node AE Title', pl: 'AeTitle - AE Title węzła' },
        'HostName - 主机名或IP地址': { en: 'HostName - hostname or IP address', pl: 'HostName - nazwa hosta lub adres IP' },
        'Port - 端口号': { en: 'Port - port number', pl: 'Port - numer portu' },
        'Description - 描述': { en: 'Description - description', pl: 'Description - opis' },
        'Type - 节点类型，可选值：': { en: 'Type - node type, possible values:', pl: 'Type - typ węzła, możliwe wartości:' },
        'store - 仅支持存储': { en: 'store - storage only', pl: 'store - tylko przechowywanie' },
        'qr - 仅支持查询检索': { en: 'qr - query/retrieve only', pl: 'qr - tylko zapytania/pobieranie' },
        'all - 支持所有操作（存储和查询检索）': { en: 'all - all operations (storage and query/retrieve)', pl: 'all - wszystkie operacje (przechowywanie oraz zapytania/pobieranie)' },
        'C-MOVE目标节点配置列表，每个节点包含：': { en: 'C-MOVE destination node list config, each node contains:', pl: 'Konfiguracja listy węzłów docelowych C-MOVE, każdy węzeł zawiera:' },
        '是否验证调用方AE Title。默认：false': { en: 'Whether to verify caller AE Title. Default: false', pl: 'Czy weryfikować AE Title wywołującego. Domyślnie: false' },
        '允许的调用方AE Title列表': { en: 'Allowed caller AE Title list', pl: 'Lista dozwolonych AE Title wywołujących' },
        '允许的调用方AE Title列表。例如：["MODALITY1", "MODALITY2"]': { en: 'Allowed caller AE Titles. E.g.: ["MODALITY1", "MODALITY2"]', pl: 'Dozwolone AE Title wywołujących. Np.: ["MODALITY1", "MODALITY2"]' },
        '是否验证调用方AE Title，启用后只允许指定的AE Title连接。默认：false': { en: 'Whether to verify caller AE Title; when enabled only the listed AE Titles may connect. Default: false', pl: 'Czy weryfikować AE Title wywołującego; po włączeniu łączyć się mogą tylko wskazane AE Title. Domyślnie: false' },
        '是否启用压缩，影响存储和传输性能。默认：false': { en: 'Whether to enable compression; affects storage and transfer performance. Default: false', pl: 'Czy włączyć kompresję; wpływa na wydajność przechowywania i transferu. Domyślnie: false' },
        '首选传输语法，当接收到压缩格式的图像时，可以选择转换为以下格式存储：': { en: 'Preferred transfer syntax: compressed images received may be converted to one of the following for storage:', pl: 'Preferowana składnia transferu: odebrane skompresowane obrazy można przekonwertować do jednego z poniższych formatów:' },
        'ExplicitVRLittleEndian - 显式小端（1.2.840.10008.1.2.1）': { en: 'ExplicitVRLittleEndian - explicit little endian (1.2.840.10008.1.2.1)', pl: 'ExplicitVRLittleEndian - jawne, little endian (1.2.840.10008.1.2.1)' },
        'ExplicitVRBigEndian - 显式大端（1.2.840.10008.1.2.2）': { en: 'ExplicitVRBigEndian - explicit big endian (1.2.840.10008.1.2.2)', pl: 'ExplicitVRBigEndian - jawne, big endian (1.2.840.10008.1.2.2)' },
        'ImplicitVRLittleEndian - 隐式小端（1.2.840.10008.1.2）': { en: 'ImplicitVRLittleEndian - implicit little endian (1.2.840.10008.1.2)', pl: 'ImplicitVRLittleEndian - niejawne, little endian (1.2.840.10008.1.2)' },
        'JPEGProcess14 - JPEG无损压缩（1.2.840.10008.1.2.4.70）': { en: 'JPEGProcess14 - JPEG lossless (1.2.840.10008.1.2.4.70)', pl: 'JPEGProcess14 - JPEG bezstratny (1.2.840.10008.1.2.4.70)' },
        'RLELossless - RLE无损压缩（1.2.840.10008.1.2.5）': { en: 'RLELossless - RLE lossless (1.2.840.10008.1.2.5)', pl: 'RLELossless - RLE bezstratny (1.2.840.10008.1.2.5)' },

        /* ===== 配置说明（打印 / 其他） ===== */
        '打印机列表配置，每个打印机包含：': { en: 'Printer list config, each printer contains:', pl: 'Konfiguracja listy drukarek, każda drukarka zawiera:' },
        'Name - 打印机名称': { en: 'Name - printer name', pl: 'Name - nazwa drukarki' },
        'AeTitle - 打印机AE Title': { en: 'AeTitle - printer AE Title', pl: 'AeTitle - AE Title drukarki' },
        'HostName - 打印机主机名或IP地址': { en: 'HostName - printer hostname or IP address', pl: 'HostName - nazwa hosta lub adres IP drukarki' },
        'Port - 打印机端口号': { en: 'Port - printer port number', pl: 'Port - numer portu drukarki' },
        'Description - 打印机描述': { en: 'Description - printer description', pl: 'Description - opis drukarki' },
        'IsDefault - 是否为默认打印机': { en: 'IsDefault - whether it is the default printer', pl: 'IsDefault - czy jest to drukarka domyślna' },
        'Web 服务器配置：': { en: 'Web server config:', pl: 'Konfiguracja serwera WWW:' },
        'Endpoints.Http.Url - 监听地址和端口，如：http://0.0.0.0:5000': { en: 'Endpoints.Http.Url - listen address and port, e.g.: http://0.0.0.0:5000', pl: 'Endpoints.Http.Url - adres i port nasłuchiwania, np.: http://0.0.0.0:5000' },
        '数据库连接字符串配置：': { en: 'Database connection string config:', pl: 'Konfiguracja ciągu połączenia bazy danych:' },
        'DicomDb - SQLite数据库文件路径': { en: 'DicomDb - SQLite database file path', pl: 'DicomDb - ścieżka pliku bazy danych SQLite' },
        'API文档配置：': { en: 'API docs config:', pl: 'Konfiguracja dokumentacji API:' },
        'Enabled - 是否启用': { en: 'Enabled - whether enabled', pl: 'Enabled - czy włączone' },
        'EnableConsoleLog - 是否输出到控制台': { en: 'EnableConsoleLog - whether to output to console', pl: 'EnableConsoleLog - czy wypisywać na konsolę' },
        'EnableFileLog - 是否输出到文件': { en: 'EnableFileLog - whether to output to file', pl: 'EnableFileLog - czy zapisywać do pliku' },
        'Title - 文档标题': { en: 'Title - document title', pl: 'Title - tytuł dokumentu' },
        'Version - 版本号': { en: 'Version - version number', pl: 'Version - numer wersji' },
        '其他 Limits（如并发连接数、超时等）使用框架默认值即可，无需在配置中指定。': { en: 'Other Limits (concurrent connections, timeouts, etc.) can keep the framework defaults; no need to specify them in config.', pl: 'Pozostałe Limits (równoległe połączenia, limity czasu itp.) mogą zachować wartości domyślne frameworku; nie trzeba ich podawać w konfiguracji.' },
        '平凡之路': { en: '平凡之路', pl: '平凡之路' },

        /* ===== 高频动态文案（JS 注入，观察器翻译） ===== */
        '获取序列数据失败:': { en: 'Failed to get series data:', pl: 'Nie udało się pobrać danych serii:' },
        '加载节点列表失败:': { en: 'Failed to load node list:', pl: 'Nie udało się wczytać listy węzłów:' },
        '加载节点列表失败': { en: 'Failed to load node list', pl: 'Nie udało się wczytać listy węzłów' },
        '请选择要测试的节点': { en: 'Please select a node to test', pl: 'Wybierz węzeł do testu' },
        '节点连接测试成功': { en: 'Node connection test succeeded', pl: 'Test połączenia z węzłem zakończony sukcesem' },
        '节点连接测试失败': { en: 'Node connection test failed', pl: 'Test połączenia z węzłem nie powiódł się' },
        '测试节点连接失败:': { en: 'Node connection test failed:', pl: 'Test połączenia z węzłem nie powiódł się:' },
        '获取预约数据失败': { en: 'Failed to get booking data', pl: 'Nie udało się pobrać danych rezerwacji' },
        '保存预约数据失败': { en: 'Failed to save booking data', pl: 'Nie udało się zapisać danych rezerwacji' },
        '找不到表单': { en: 'Form not found', pl: 'Nie znaleziono formularza' },
        '找不到表单元素': { en: 'Form element not found', pl: 'Nie znaleziono elementu formularza' },
        '格式化日期时间失败:': { en: 'Failed to format date/time:', pl: 'Nie udało się sformatować daty/czasu:' },
        '处理文件失败:': { en: 'Failed to process files:', pl: 'Nie udało się przetworzyć plików:' },
        '加载影像失败': { en: 'Failed to load images', pl: 'Nie udało się wczytać obrazów' },
        '暂无影像数据': { en: 'No image data', pl: 'Brak danych obrazów' },
        '图像加载失败': { en: 'Failed to load image', pl: 'Nie udało się wczytać obrazu' },
        '预览图像失败': { en: 'Failed to preview image', pl: 'Nie udało się otworzyć podglądu obrazu' },
        '打开Weasis失败': { en: 'Failed to open Weasis', pl: 'Nie udało się otworzyć Weasis' },
        '任务详情': { en: 'Job Details', pl: 'Szczegóły zadania' },
        '无损JPEG': { en: 'Lossless JPEG', pl: 'JPEG bezstratny' },
        '无损JPEG2000': { en: 'Lossless JPEG2000', pl: 'JPEG2000 bezstratny' },
        '加载配置失败:': { en: 'Failed to load config:', pl: 'Nie udało się wczytać konfiguracji:' },
        '获取配置失败': { en: 'Failed to get config', pl: 'Nie udało się pobrać konfiguracji' },
        '加载帮助内容失败:': { en: 'Failed to load help content:', pl: 'Nie udało się wczytać treści pomocy:' },
        '加载帮助内容失败': { en: 'Failed to load help content', pl: 'Nie udało się wczytać treści pomocy' },
        '绑定认证事件失败:': { en: 'Failed to bind auth events:', pl: 'Nie udało się powiązać zdarzeń uwierzytelniania:' },
        '找不到修改密码弹窗元素': { en: 'Change-password dialog element not found', pl: 'Nie znaleziono okna zmiany hasła' },
        '显示修改密码对话框失败:': { en: 'Failed to show change-password dialog:', pl: 'Nie udało się wyświetlić okna zmiany hasła:' },
        '显示修改密码对话框失败': { en: 'Failed to show change-password dialog', pl: 'Nie udało się wyświetlić okna zmiany hasła' },
        '登出失败:': { en: 'Sign-out failed:', pl: 'Wylogowanie nie powiodło się:' },
        '登出失败': { en: 'Sign-out failed', pl: 'Wylogowanie nie powiodło się' },

        /* ===== 通用操作 / 状态补充 ===== */
        '保存中...': { en: 'Saving...', pl: 'Zapisywanie...' },
        '测试中...': { en: 'Testing...', pl: 'Testowanie...' },
        '正在扫描文件...': { en: 'Scanning files...', pl: 'Skanowanie plików...' },
        '正在加载数据...': { en: 'Loading data...', pl: 'Wczytywanie danych...' },
        '打印': { en: 'Print', pl: 'Drukuj' },
        '成功': { en: 'Success', pl: 'Powodzenie' },
        '是': { en: 'Yes', pl: 'Tak' },
        '否': { en: 'No', pl: 'Nie' },
        '未设置': { en: 'Not set', pl: 'Nie ustawiono' },
        '操作失败': { en: 'Operation failed', pl: 'Operacja nie powiodła się' },
        '更新成功': { en: 'Updated successfully', pl: 'Zaktualizowano pomyślnie' },
        '更新失败': { en: 'Update failed', pl: 'Aktualizacja nie powiodła się' },
        '日志文件已删除': { en: 'Log file deleted', pl: 'Plik logów usunięty' },
        '预约已添加': { en: 'Booking added', pl: 'Dodano rezerwację' },
        '预约已更新': { en: 'Booking updated', pl: 'Zaktualizowano rezerwację' },
        '编辑预约': { en: 'Edit Booking', pl: 'Edytuj rezerwację' },
        '加载日志类型失败': { en: 'Failed to load log types', pl: 'Nie udało się wczytać typów logów' },
        '加载日志类型失败:': { en: 'Failed to load log types:', pl: 'Nie udało się wczytać typów logów:' },
        '加载日志文件失败': { en: 'Failed to load log files', pl: 'Nie udało się wczytać plików logów' },
        '加载日志文件失败:': { en: 'Failed to load log files:', pl: 'Nie udało się wczytać plików logów:' },
        '绑定事件失败': { en: 'Failed to bind events', pl: 'Nie udało się powiązać zdarzeń' },
        '显示数据失败': { en: 'Failed to display data', pl: 'Nie udało się wyświetlić danych' },
        '找不到表格主体': { en: 'Table body not found', pl: 'Nie znaleziono treści tabeli' },
        '返回数据格式错误': { en: 'Invalid data format returned', pl: 'Nieprawidłowy format zwróconych danych' },
        '分页信息格式错误': { en: 'Invalid pagination info', pl: 'Nieprawidłowe informacje paginacji' },
        '打开添加预约窗口失败': { en: 'Failed to open the Add Booking dialog', pl: 'Nie udało się otworzyć okna dodawania rezerwacji' },
        '绑定QR分页事件失败:': { en: 'Failed to bind QR pagination events:', pl: 'Nie udało się powiązać zdarzeń paginacji QR:' },
        '初始化打印管理器失败:': { en: 'Failed to initialize the print manager:', pl: 'Nie udało się zainicjalizować menedżera drukowania:' },
        '初始化模态框管理器失败:': { en: 'Failed to initialize the modal manager:', pl: 'Nie udało się zainicjalizować menedżera okien dialogowych:' },
        '关闭所有模态框失败:': { en: 'Failed to close all modals:', pl: 'Nie udało się zamknąć okien dialogowych:' },
        '显示对话框失败:': { en: 'Failed to show dialog:', pl: 'Nie udało się wyświetlić okna dialogowego:' },
        '找不到 Toast 元素': { en: 'Toast element not found', pl: 'Nie znaleziono elementu powiadomienia' },
        '找不到 Toast 内部元素': { en: 'Toast inner element not found', pl: 'Nie znaleziono wewnętrznego elementu powiadomienia' },
        '显示提示失败:': { en: 'Failed to show notice:', pl: 'Nie udało się wyświetlić powiadomienia:' },
        '会话已过期，请重新登录': { en: 'Session expired, please sign in again', pl: 'Sesja wygasła, zaloguj się ponownie' },
        '切换页面失败:': { en: 'Failed to switch page:', pl: 'Nie udało się przełączyć strony:' },
        '页面切换失败': { en: 'Page switch failed', pl: 'Przełączenie strony nie powiodło się' },
        '未知的页面: {0}': { en: 'Unknown page: {0}', pl: 'Nieznana strona: {0}' },
        '初始化页面 {0} 失败:': { en: 'Initializing page {0} failed:', pl: 'Inicjalizacja strony {0} nie powiodła się:' },
        '初始化页面失败: {0}': { en: 'Page initialization failed: {0}', pl: 'Inicjalizacja strony nie powiodła się: {0}' },

        /* ===== 服务 / 系统信息 ===== */
        '系统信息': { en: 'System Info', pl: 'Informacje o systemie' },
        'CPU型号': { en: 'CPU Model', pl: 'Model procesora' },
        'CPU使用率': { en: 'CPU Usage', pl: 'Użycie procesora' },
        '系统内存': { en: 'System Memory', pl: 'Pamięć systemowa' },
        '程序内存': { en: 'Process Memory', pl: 'Pamięć procesu' },
        '运行时间': { en: 'Uptime', pl: 'Czas działania' },
        '操作系统': { en: 'OS', pl: 'System operacyjny' },
        'DICOM 服务状态': { en: 'DICOM Service Status', pl: 'Status usług DICOM' },
        '存储服务 (StoreSCP)': { en: 'Storage Service (StoreSCP)', pl: 'Usługa przechowywania (StoreSCP)' },
        '检查列表 (WorklistSCP)': { en: 'Worklist Service (WorklistSCP)', pl: 'Usługa listy badań (WorklistSCP)' },
        '查询服务 (QRSCP)': { en: 'Query/Retrieve Service (QRSCP)', pl: 'Usługa zapytań/pobierania (QRSCP)' },
        '打印服务 (PrintSCP)': { en: 'Print Service (PrintSCP)', pl: 'Usługa drukowania (PrintSCP)' },
        '系统和服务状态': { en: 'System & Service Status', pl: 'Status systemu i usług' },
        '运行中': { en: 'Running', pl: 'Działa' },
        '已停止': { en: 'Stopped', pl: 'Zatrzymano' },
        '获取服务状态失败:': { en: 'Failed to get service status:', pl: 'Nie udało się pobrać statusu usług:' },
        '更新系统信息失败:': { en: 'Failed to update system info:', pl: 'Nie udało się zaktualizować informacji o systemie:' },
        '更新运行时间失败:': { en: 'Failed to update uptime:', pl: 'Nie udało się zaktualizować czasu działania:' },
        '更新服务状态失败: {0}': { en: 'Failed to update service status: {0}', pl: 'Nie udało się zaktualizować statusu usług: {0}' },
        'AET: {0} 端口: {1}': { en: 'AET: {0} Port: {1}', pl: 'AET: {0} Port: {1}' },
        '{0}岁': { en: '{0}Y', pl: '{0} lat' },
        '岁': { en: 'Y', pl: 'lat' },
        '{0}天': { en: '{0}d', pl: '{0}d' },
        '{0}小时': { en: '{0}h', pl: '{0}godz' },
        '{0}分钟': { en: '{0}min', pl: '{0}min' },
        '{0}天{1}小时{2}分钟': { en: '{0}d {1}h {2}m', pl: '{0}d {1}godz {2}min' },
        '{0}小时{1}分钟': { en: '{0}h {1}m', pl: '{0}godz {1}min' },

        /* ===== 文件发送 ===== */
        '待发送': { en: 'Pending', pl: 'Oczekujące' },
        '发送中': { en: 'Sending', pl: 'Wysyłanie' },
        '已发送': { en: 'Sent', pl: 'Wysłane' },
        '发送失败': { en: 'Send failed', pl: 'Wysyłanie nie powiodło się' },
        '请选择目标节点': { en: 'Please select a destination node', pl: 'Wybierz węzeł docelowy' },
        '请选择PACS节点': { en: 'Please select a PACS node', pl: 'Wybierz węzeł PACS' },
        '选择目标节点': { en: 'Select Destination Node', pl: 'Wybierz węzeł docelowy' },
        '没有需要发送的文件': { en: 'No files to send', pl: 'Brak plików do wysłania' },
        '处理文件失败': { en: 'Failed to process files', pl: 'Nie udało się przetworzyć plików' },
        '处理目录失败:': { en: 'Failed to process directory:', pl: 'Nie udało się przetworzyć katalogu:' },
        '读取目录失败:': { en: 'Failed to read directory:', pl: 'Nie udało się odczytać katalogu:' },
        '发送失败:': { en: 'Send failed:', pl: 'Wysyłanie nie powiodło się:' },
        '正在扫描文件夹: {0}': { en: 'Scanning folder: {0}', pl: 'Skanowanie folderu: {0}' },
        '文件夹 "{0}" 中包含 {1} 个DICOM文件': { en: 'Folder "{0}" contains {1} DICOM file(s)', pl: 'Folder "{0}" zawiera {1} plik(i/ów) DICOM' },
        '文件夹 "{0}" 中包含 {1} 个DICOM文件，跳过 {2} 个非DICOM文件': { en: 'Folder "{0}" contains {1} DICOM file(s), skipped {2} non-DICOM file(s)', pl: 'Folder "{0}" zawiera {1} plik(i/ów) DICOM, pominięto {2} plik(i/ów) nie-DICOM' },
        '已添加 {0} 个DICOM文件': { en: 'Added {0} DICOM file(s)', pl: 'Dodano {0} plik(i/ów) DICOM' },
        '已添加 {0} 个DICOM文件，跳过 {1} 个非DICOM文件': { en: 'Added {0} DICOM file(s), skipped {1} non-DICOM file(s)', pl: 'Dodano {0} plik(i/ów) DICOM, pominięto {1} plik(i/ów) nie-DICOM' },
        '，跳过 {0} 个非DICOM文件': { en: ', skipped {0} non-DICOM file(s)', pl: ', pominięto {0} plik(i/ów) nie-DICOM' },
        '总计: {0} 个文件': { en: 'Total: {0} file(s)', pl: 'Razem: {0} plik(i/ów)' },
        '待发送: {0}': { en: 'Pending: {0}', pl: 'Oczekujące: {0}' },
        '发送中: {0}': { en: 'Sending: {0}', pl: 'Wysyłanie: {0}' },
        '已完成: {0}': { en: 'Completed: {0}', pl: 'Zakończono: {0}' },
        '失败: {0}': { en: 'Failed: {0}', pl: 'Niepowodzenie: {0}' },
        '发送完成：{0} 个成功': { en: 'Sent: {0} succeeded', pl: 'Wysłano: {0} zakończonych sukcesem' },
        '发送完成：{0} 个成功，{1} 个失败': { en: 'Sent: {0} succeeded, {1} failed', pl: 'Wysłano: {0} zakończonych sukcesem, {1} niepowodzeń' },

        /* ===== 查看器 ===== */
        '暂停': { en: 'Pause', pl: 'Wstrzymaj' },
        '播放': { en: 'Play', pl: 'Odtwórz' },
        '帧号': { en: 'Frame No.', pl: 'Nr klatki' },
        '图像号': { en: 'Image No.', pl: 'Nr obrazu' },
        '无损JPEG-LS': { en: 'Lossless JPEG-LS', pl: 'JPEG-LS bezstratny' },
        '无损RLE': { en: 'Lossless RLE', pl: 'RLE bezstratny' },
        '加载图像失败': { en: 'Failed to load image', pl: 'Nie udało się wczytać obrazu' },
        '显示图像失败:': { en: 'Failed to display image:', pl: 'Nie udało się wyświetlić obrazu:' },
        '字符解码失败:': { en: 'Failed to decode text:', pl: 'Nie udało się zdekodować tekstu:' },
        '窗宽: {0}': { en: 'Window: {0}', pl: 'Okno: {0}' },
        '窗位: {0}': { en: 'Level: {0}', pl: 'Poziom: {0}' },
        'ID: {0}': { en: 'ID: {0}', pl: 'ID: {0}' },
        '性别: {0}': { en: 'Sex: {0}', pl: 'Płeć: {0}' },
        '检查号: {0}': { en: 'Accession: {0}', pl: 'Numer badania: {0}' },
        '时间：{0}': { en: 'Time: {0}', pl: 'Czas: {0}' },
        '类型: {0}': { en: 'Type: {0}', pl: 'Typ: {0}' },
        '描述: {0}': { en: 'Description: {0}', pl: 'Opis: {0}' },
        '序列号: {0}': { en: 'Series: {0}', pl: 'Seria: {0}' },
        '帧号: {0}': { en: 'Frame: {0}', pl: 'Klatka: {0}' },
        '图像号: {0}': { en: 'Image: {0}', pl: 'Obraz: {0}' },
        '{0}/{1}': { en: '{0}/{1}', pl: '{0}/{1}' },
        '© {0} DICOM管理系统 by': { en: '© {0} DICOM Management by', pl: '© {0} Zarządzanie DICOM by' },

        /* ===== 查询检索 ===== */
        '查询失败': { en: 'Query failed', pl: 'Zapytanie nie powiodło się' },
        '查询失败:': { en: 'Query failed:', pl: 'Zapytanie nie powiodło się:' },
        '查询失败，请重试': { en: 'Query failed, please retry', pl: 'Zapytanie nie powiodło się, spróbuj ponownie' },
        '未找到匹配的检查': { en: 'No matching studies found', pl: 'Nie znaleziono pasujących badań' },
        '请输入查询条件': { en: 'Enter a search condition', pl: 'Wpisz warunek wyszukiwania' },
        '获取序列数据失败': { en: 'Failed to get series data', pl: 'Nie udało się pobrać danych serii' },
        '获取失败:': { en: 'Retrieve failed:', pl: 'Pobieranie nie powiodło się:' },
        '确定要获取选中的检查吗？': { en: 'Retrieve the selected study?', pl: 'Pobrać wybrane badanie?' },
        '确定要获取选中的序列吗？': { en: 'Retrieve the selected series?', pl: 'Pobrać wybraną serię?' },
        '检查获取请求已发送，请稍后在影像管理中查看！': { en: 'Retrieve request sent; check Image Management shortly!', pl: 'Wysłano żądanie pobrania; sprawdź wkrótce Zarządzanie obrazami!' },
        '序列获取请求已发送，请稍后在影像管理中查看！': { en: 'Series retrieve request sent; check Image Management shortly!', pl: 'Wysłano żądanie pobrania serii; sprawdź wkrótce Zarządzanie obrazami!' },

        /* ===== 影像管理 / 编辑检查 ===== */
        'OHIF预览': { en: 'OHIF Preview', pl: 'Podgląd OHIF' },
        'Weasis预览': { en: 'Weasis Preview', pl: 'Podgląd Weasis' },
        '编辑基本信息': { en: 'Edit Basic Info', pl: 'Edytuj podstawowe informacje' },
        '编辑检查基本信息': { en: 'Edit Study Basic Info', pl: 'Edytuj podstawowe informacje badania' },
        'OHIF 查看器': { en: 'OHIF Viewer', pl: 'Przeglądarka OHIF' },
        '绑定影像管理事件失败:': { en: 'Failed to bind image management events:', pl: 'Nie udało się powiązać zdarzeń zarządzania obrazami:' },
        '确定要删除这个检查吗？此操作不可恢复。': { en: 'Delete this study? This cannot be undone.', pl: 'Usunąć to badanie? Tej operacji nie można cofnąć.' },
        '找不到元素: {0}': { en: 'Element not found: {0}', pl: 'Nie znaleziono elementu: {0}' },
        '患者姓名': { en: 'Patient Name', pl: 'Imię i nazwisko pacjenta' },
        '生日': { en: 'Birth Date', pl: 'Data urodzenia' },
        '机构': { en: 'Institution', pl: 'Instytucja' },
        '备注': { en: 'Remark', pl: 'Uwaga' },
        '男(M)': { en: 'Male (M)', pl: 'Mężczyzna (M)' },
        '女(F)': { en: 'Female (F)', pl: 'Kobieta (F)' },
        '其他(O)': { en: 'Other (O)', pl: 'Inne (O)' },
        '序列号': { en: 'Series No.', pl: 'Nr serii' },
        '序列描述': { en: 'Series Description', pl: 'Opis serii' },
        '图像数量': { en: 'Images', pl: 'Liczba obrazów' },
        '暂无序列数据': { en: 'No series data', pl: 'Brak danych serii' },

        /* ===== 打印 ===== */
        '暂无打印任务': { en: 'No print jobs', pl: 'Brak zadań drukowania' },
        '正在加载中，跳过重复请求': { en: 'Loading, ignoring repeated request', pl: 'Wczytywanie, pomijanie powtórnego żądania' },
        '找不到打印任务列表元素': { en: 'Print job list element not found', pl: 'Nie znaleziono elementu listy zadań drukowania' },
        '加载打印任务失败:': { en: 'Failed to load print jobs:', pl: 'Nie udało się wczytać zadań drukowania:' },
        '预览图像失败:': { en: 'Failed to preview image:', pl: 'Nie udało się otworzyć podglądu obrazu:' },
        '获取任务详情失败': { en: 'Failed to get job details', pl: 'Nie udało się pobrać szczegółów zadania' },
        '获取任务详情失败:': { en: 'Failed to get job details:', pl: 'Nie udało się pobrać szczegółów zadania:' },
        '确定要删除这个打印任务吗？': { en: 'Delete this print job?', pl: 'Usunąć to zadanie drukowania?' },
        '删除打印任务失败:': { en: 'Failed to delete print job:', pl: 'Nie udało się usunąć zadania drukowania:' },
        '暂无可用打印机': { en: 'No printers available', pl: 'Brak dostępnych drukarek' },
        '可用打印机': { en: 'Available Printers', pl: 'Dostępne drukarki' },
        '选择打印机': { en: 'Select Printer', pl: 'Wybierz drukarkę' },
        '请选择打印机': { en: 'Please select a printer', pl: 'Wybierz drukarkę' },
        '找不到打印机选择框': { en: 'Printer selector not found', pl: 'Nie znaleziono wyboru drukarki' },
        '显示打印机选择对话框失败': { en: 'Failed to show the printer dialog', pl: 'Nie udało się wyświetlić okna wyboru drukarki' },
        '显示打印机选择对话框失败:': { en: 'Failed to show the printer dialog:', pl: 'Nie udało się wyświetlić okna wyboru drukarki:' },
        '打印任务已发送': { en: 'Print job sent', pl: 'Zadanie drukowania wysłane' },
        '打印失败': { en: 'Print failed', pl: 'Drukowanie nie powiodło się' },
        '打印失败:': { en: 'Print failed:', pl: 'Drukowanie nie powiodło się:' },
        '加载打印机列表失败': { en: 'Failed to load printer list', pl: 'Nie udało się wczytać listy drukarek' },
        '加载打印机列表失败:': { en: 'Failed to load printer list:', pl: 'Nie udało się wczytać listy drukarek:' },
        '总数: {0}': { en: 'Total: {0}', pl: 'Razem: {0}' },
        '已创建: {0}': { en: 'Created: {0}', pl: 'Utworzono: {0}' },
        '已接收: {0}': { en: 'Received: {0}', pl: 'Odebrano: {0}' },
        '基本信息': { en: 'Basic Info', pl: 'Informacje podstawowe' },
        '错误信息': { en: 'Error Info', pl: 'Informacje o błędzie' },
        'Film Session 参数': { en: 'Film Session Parameters', pl: 'Parametry sesji filmu' },
        '打印份数': { en: 'Copy Count', pl: 'Liczba kopii' },
        '打印优先级': { en: 'Print Priority', pl: 'Priorytet drukowania' },
        '介质类型': { en: 'Medium Type', pl: 'Typ nośnika' },
        '胶片目标': { en: 'Film Destination', pl: 'Cel filmu' },
        'Film Box 参数': { en: 'Film Box Parameters', pl: 'Parametry pudełka filmu' },
        '彩色打印': { en: 'Color Print', pl: 'Drukowanie kolorowe' },
        '胶片方向': { en: 'Film Orientation', pl: 'Orientacja filmu' },
        '胶片尺寸': { en: 'Film Size', pl: 'Rozmiar filmu' },
        '图像显示格式': { en: 'Image Display Format', pl: 'Format wyświetlania obrazów' },
        '放大类型': { en: 'Magnification Type', pl: 'Typ powiększenia' },
        '边框密度': { en: 'Border Density', pl: 'Gęstość obramowania' },
        '空图像密度': { en: 'Empty Image Density', pl: 'Gęstość pustego obrazu' },
        '最小密度': { en: 'Min Density', pl: 'Minimalna gęstość' },
        '最大密度': { en: 'Max Density', pl: 'Maksymalna gęstość' },
        '其他信息': { en: 'Other Info', pl: 'Inne informacje' },
        '检查实例UID': { en: 'Study Instance UID', pl: 'UID instancji badania' },
        '图像路径': { en: 'Image Path', pl: 'Ścieżka obrazu' },
        '更新时间': { en: 'Updated At', pl: 'Czas aktualizacji' },

        /* ===== 预约登记 ===== */
        '暂无预约检查': { en: 'No scheduled studies', pl: 'Brak zarezerwowanych badań' },
        '请输入有效的年龄（0-150岁）': { en: 'Enter a valid age (0-150)', pl: 'Wpisz prawidłowy wiek (0-150)' },
        '预约时间不能早于当前时间': { en: 'Scheduled time cannot be earlier than now', pl: 'Czas rezerwacji nie może być wcześniejszy niż teraz' },
        '确定要删除这个预约吗？此操作不可恢复。': { en: 'Delete this booking? This cannot be undone.', pl: 'Usunąć tę rezerwację? Tej operacji nie można cofnąć.' },
        '{0} 不能为空': { en: '{0} must not be empty', pl: '{0} nie może być puste' },
        '格式化显示日期时间失败:': { en: 'Failed to format display date/time:', pl: 'Nie udało się sformatować daty/czasu:' },
        '查询日期 - 原始值:': { en: 'Query date - raw value:', pl: 'Data zapytania - wartość surowa:' },
        '查询日期 - 格式化后:': { en: 'Query date - formatted:', pl: 'Data zapytania - po formatowaniu:' },
        '查询参数:': { en: 'Query parameters:', pl: 'Parametry zapytania:' },

        /* ===== 配置 ===== */
        '确定要保存当前配置吗？保存后需要重启服务生效。': { en: 'Save the current config? The service must be restarted to apply it.', pl: 'Zapisać bieżącą konfigurację? Aby zastosować, należy zrestartować usługę.' },
        '确定要删除日志文件 {0} 吗？': { en: 'Delete log file {0}?', pl: 'Usunąć plik logów {0}?' },

        /* ===== 帮助页（help.html 长串） ===== */
        '允许的调用方AE Title列表，当ValidateCallingAE为true时生效。例如：["WORKSTATION1", "PACS1"]': { en: 'List of allowed caller AE Titles; effective when ValidateCallingAE is true. E.g.: ["WORKSTATION1", "PACS1"]', pl: 'Lista dozwolonych AE Title wywołujących; działa, gdy ValidateCallingAE ma wartość true. Np.: ["WORKSTATION1", "PACS1"]' },
        'JPEG2000Lossless - JPEG2000无损压缩（1.2.840.10008.1.2.4.90）': { en: 'JPEG2000Lossless - JPEG2000 lossless compression (1.2.840.10008.1.2.4.90)', pl: 'JPEG2000Lossless - kompresja bezstratna JPEG2000 (1.2.840.10008.1.2.4.90)' },
        'JPEGLSLossless - JPEG-LS无损压缩（1.2.840.10008.1.2.4.80）': { en: 'JPEGLSLossless - JPEG-LS lossless compression (1.2.840.10008.1.2.4.80)', pl: 'JPEGLSLossless - kompresja bezstratna JPEG-LS (1.2.840.10008.1.2.4.80)' },
        'JPEG2000Lossy - JPEG2000有损压缩（1.2.840.10008.1.2.4.91）': { en: 'JPEG2000Lossy - JPEG2000 lossy compression (1.2.840.10008.1.2.4.91)', pl: 'JPEG2000Lossy - kompresja stratna JPEG2000 (1.2.840.10008.1.2.4.91)' },
        '日志输出模板。默认：[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}': { en: 'Log output template. Default: [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}', pl: 'Szablon wyjściowy logów. Domyślnie: [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}' },
        '注意：设置某个级别后，将记录该级别及更高级别的日志。例如，设置为 Information 将记录 Information、Warning、Error 和 Fatal 级别的日志。': { en: 'Note: once a level is set, logs at that level and above are recorded. For example, setting Information records Information, Warning, Error and Fatal levels.', pl: 'Uwaga: po ustawieniu poziomu rejestrowane są logi tego poziomu i wyższych. Na przykład ustawienie Information rejestruje poziomy Information, Warning, Error i Fatal.' },
        'Limits.MaxRequestBodySize - 单次请求 body 最大字节数，如 524288000 表示 500MB。用于限制上传大小，DICOM 上传建议设大一些': { en: 'Limits.MaxRequestBodySize - max request body size in bytes; e.g. 524288000 is 500MB. Limits upload size; set larger for DICOM uploads', pl: 'Limits.MaxRequestBodySize - maksymalny rozmiar treści żądania w bajtach, np. 524288000 to 500MB. Ogranicza rozmiar przesyłanych plików; dla uploadu DICOM zaleca się większą wartość' }
    };

    function dict(lang) {
        if (lang === 'zh') { return null; }
        return STRINGS;
    }

    function normalize(text) {
        return String(text).replace(/\s+/g, ' ').trim();
    }

    var tokenPattern = /\{(\d+)\}/g;
    var PATTERN_ITEMS = [];
    var PATTERNS_BUILT = false;

    function escapeRegExp(s) {
        return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    /* 带占位符的模板词条 → 正则。长键优先，避免短模板误吞长文本 */
    function buildPatterns() {
        if (PATTERNS_BUILT) { return; }
        PATTERNS_BUILT = true;
        var keys = [];
        for (var key in STRINGS) {
            if (STRINGS.hasOwnProperty(key) && /\{\d+\}/.test(key)) { keys.push(key); }
        }
        keys.sort(function (a, b) { return b.length - a.length; });
        for (var i = 0; i < keys.length; i++) {
            var k = keys[i];
            var reSrc = '';
            var capMap = [];
            var capIndex = 0;
            var last = 0;
            var m;
            tokenPattern.lastIndex = 0;
            while ((m = tokenPattern.exec(k)) !== null) {
                reSrc += escapeRegExp(k.slice(last, m.index));
                reSrc += '(.+?)';
                capIndex++;
                capMap[Number(m[1])] = capIndex;
                last = m.index + m[0].length;
            }
            reSrc += escapeRegExp(k.slice(last));
            PATTERN_ITEMS.push({
                key: k,
                re: new RegExp('^' + reSrc + '$'),
                capMap: capMap
            });
        }
    }

    /* 模板匹配翻译：捕获片段若仍是中文则递归翻译 */
    function translateByPattern(text) {
        buildPatterns();
        for (var i = 0; i < PATTERN_ITEMS.length; i++) {
            var item = PATTERN_ITEMS[i];
            var m = item.re.exec(text);
            if (!m) { continue; }
            var entry = STRINGS[item.key];
            var translated = entry ? entry[currentLang] : null;
            if (!translated) { return null; }
            return translated.replace(/\{(\d+)\}/g, function (full, idx) {
                var capIdx = item.capMap[Number(idx)];
                var captured = m[capIdx];
                if (captured == null) { return full; }
                var sub = translateText(captured);
                return sub !== null ? sub : captured;
            });
        }
        return null;
    }

    function translateText(text) {
        var d = dict(currentLang);
        if (!d) { return null; }
        var key = normalize(text);
        if (!key) { return null; }
        if (Object.prototype.hasOwnProperty.call(d, key)) {
            var entry = d[key];
            return entry ? entry[currentLang] : null;
        }
        return translateByPattern(key);
    }

    function translateTextNode(node) {
        var original = node.nodeValue;
        if (!original || !/[\u4e00-\u9fa5]/.test(original)) { return; }
        if (currentLang === 'zh') {
            if (node.__i18nZH != null && node.nodeValue !== node.__i18nZH) {
                node.nodeValue = node.__i18nZH;
            }
            return;
        }
        if (node.__i18nZH == null) { node.__i18nZH = original; }
        var translation = translateText(original);
        if (translation && translation !== normalize(original)) {
            /* 保留原文前后空白，避免破坏行内布局 */
            var lead = original.match(/^\s*/)[0];
            var tail = original.match(/\s*$/)[0];
            node.nodeValue = lead + translation + tail;
        }
    }

    var TRANSLATABLE_ATTRS = ['placeholder', 'title', 'aria-label'];

    function translateElementAttributes(el) {
        for (var i = 0; i < TRANSLATABLE_ATTRS.length; i++) {
            var attr = TRANSLATABLE_ATTRS[i];
            var value = el.getAttribute && el.getAttribute(attr);
            if (!value) { continue; }
            if (currentLang === 'zh') {
                if (el.__i18nAttrs
                        && Object.prototype.hasOwnProperty.call(el.__i18nAttrs, attr)
                        && el.getAttribute(attr) !== el.__i18nAttrs[attr]) {
                    el.setAttribute(attr, el.__i18nAttrs[attr]);
                }
                continue;
            }
            if (!el.__i18nAttrs) { el.__i18nAttrs = {}; }
            if (!Object.prototype.hasOwnProperty.call(el.__i18nAttrs, attr)) {
                el.__i18nAttrs[attr] = value;
            }
            var translation = translateText(value);
            if (translation) {
                el.setAttribute(attr, translation);
            }
        }
    }

    function isSkippable(node) {
        var name = node.nodeName;
        return name === 'SCRIPT' || name === 'STYLE' || name === 'CODE' || name === 'PRE';
    }

    function applyToTree(root) {
        if (!root) { return; }
        if (root.nodeType === Node.TEXT_NODE) {
            if (root.parentNode && !isSkippable(root.parentNode)) { translateTextNode(root); }
            return;
        }
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
        var node = walker.nextNode();
        var batch = [];
        while (node) {
            if (!isSkippable(node.parentNode)) { batch.push(node); }
            node = walker.nextNode();
        }
        for (var i = 0; i < batch.length; i++) { translateTextNode(batch[i]); }
        var elements = root.querySelectorAll ? root.querySelectorAll('*') : [];
        for (var j = 0; j < elements.length; j++) { translateElementAttributes(elements[j]); }
        if (root.nodeType === Node.ELEMENT_NODE) { translateElementAttributes(root); }
    }

    var observer = null;
    var pendingNodes = [];
    var scheduled = false;

    function scheduleApply() {
        if (scheduled) { return; }
        scheduled = true;
        setTimeout(function () {
            scheduled = false;
            var roots = pendingNodes.slice();
            pendingNodes.length = 0;
            for (var i = 0; i < roots.length; i++) { applyToTree(roots[i]); }
        }, 80);
    }

    function startObserver() {
        if (observer || !window.MutationObserver) { return; }
        observer = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                var added = mutations[i].addedNodes;
                for (var j = 0; j < added.length; j++) {
                    if (added[j].nodeType === Node.ELEMENT_NODE || added[j].nodeType === Node.TEXT_NODE) {
                        pendingNodes.push(added[j]);
                    }
                }
                if (pendingNodes.length) { scheduleApply(); }
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    function detectLang() {
        var saved = null;
        try { saved = localStorage.getItem(STORAGE_KEY); } catch (e) { /* 隐私模式忽略 */ }
        if (saved && SUPPORTED.indexOf(saved) !== -1) { return saved; }
        var nav = (navigator.language || 'zh').toLowerCase();
        if (nav.indexOf('pl') === 0) { return 'pl'; }
        if (nav.indexOf('en') === 0) { return 'en'; }
        return 'zh';
    }

    function getLang() { return currentLang; }

    function resolveTitle() {
        if (!originalTitle) { return document.title; }
        return currentLang === 'zh' ? originalTitle : (translateText(originalTitle) || originalTitle);
    }

    function setLang(lang) {
        if (SUPPORTED.indexOf(lang) === -1) { return; }
        currentLang = lang;
        try { localStorage.setItem(STORAGE_KEY, lang); } catch (e) { /* 隐私模式忽略 */ }
        document.documentElement.lang = lang === 'zh' ? 'zh-CN' : lang;
        applyToTree(document.body);
        document.title = resolveTitle();
        document.dispatchEvent(new CustomEvent('i18n:changed', { detail: { lang: lang } }));
    }

    function t(text) {
        return translateText(text) || text;
    }

    /* 语言切换器：挂载到页面上的 #i18n-switcher 容器 */
    function mountSwitcher() {
        var mounts = document.querySelectorAll('#i18n-switcher');
        for (var i = 0; i < mounts.length; i++) { renderSwitcher(mounts[i]); }
    }

    function renderSwitcher(container) {
        var select = document.createElement('select');
        select.className = 'form-select form-select-sm i18n-switcher';
        select.setAttribute('aria-label', 'Language');
        var labels = { zh: '中文', en: 'English', pl: 'Polski' };
        for (var i = 0; i < SUPPORTED.length; i++) {
            var lang = SUPPORTED[i];
            var option = document.createElement('option');
            option.value = lang;
            option.textContent = labels[lang];
            if (lang === currentLang) { option.selected = true; }
            select.appendChild(option);
        }
        select.addEventListener('change', function () { setLang(select.value); });
        container.innerHTML = '';
        container.appendChild(select);
    }

    function init() {
        currentLang = detectLang();
        document.documentElement.lang = currentLang === 'zh' ? 'zh-CN' : currentLang;
        var start = function () {
            applyToTree(document.body);
            document.title = resolveTitle();
            mountSwitcher();
            startObserver();
        };
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', start);
        } else {
            start();
        }
    }

    return {
        t: t,
        getLang: getLang,
        setLang: setLang,
        apply: function () { applyToTree(document.body); },
        mountSwitcher: mountSwitcher,
        supported: SUPPORTED.slice(),
        init: init
    };
}());

window.I18N.init();
