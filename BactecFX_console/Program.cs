using System;
using System.Text;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Text.RegularExpressions;
using System.Configuration;
using System.Data.SqlClient;
using System.Collections.Generic;
using Telegram.Bot;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Threading.Tasks;
using System.Net;


namespace BactecFX_console
{
    class Program
    {
        #region settings
        public static string COMPortName = "COM3"; // порт из nport administration
        public static bool ServiceIsActive;        // флаг для запуска и остановки потока
        public static bool FileToErrorPath;        // флаг для перемещения файлов в ошибки или архив

        static bool _continue;
        static SerialPort _serialPort;
        static int WaitTimeOut = 50;

        public static List<Thread> ListOfThreads = new List<Thread>(); // список работающих потоков 
        static object ExchangeLogLocker = new object();    // локер для логов обмена
        static object FileResultLogLocker = new object();  //локер для логов функции
        static object ServiceLogLocker = new object();     //локер для логов драйвера
        public static string AnalyzerResultPath = AppDomain.CurrentDomain.BaseDirectory + "\\AnalyzerResults"; // папка для файлов с результатами

        public static string user = "PSMExchangeUser"; //логин для базы обмена файлами и для базы CGM Analytix
        public static string password = "PSM_123456"; //пароль для базы обмена файлами и для базы CGM Analytix
                                                      
         // токен бота
         public static TelegramBotClient botClient = new TelegramBotClient("5713460548:AAHAem3It_bVQQrMcRvX2QNy7n5m_IUqLMY");


        #endregion

        #region Управляющие биты
        static byte[] STX = { 0x02 }; // начало текста
        static byte[] ETX = { 0x03 }; // конец текста
        static byte[] EOT = { 0x04 }; // конец передачи данных
        static byte[] ENQ = { 0x05 }; // запрос 
        static byte[] ACK = { 0x06 }; // подтверждение
        static byte[] NAK = { 0x15 };
        static byte[] SYN = { 0x16 };
        static byte[] ETB = { 0x17 };
        static byte[] LF = { 0x0A };
        static byte[] CR = { 0x0D };

        #endregion

        #region функции логов

        // Лог обмена с прибором
        static void ExchangeLog(string Message)
        {
            lock(ExchangeLogLocker)
            {
                string path = AppDomain.CurrentDomain.BaseDirectory + "\\Log\\Exchange";
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                string filename = path + "\\ExchangeThread_" + DateTime.Now.Date.ToShortDateString().Replace('/', '_') + ".txt";
                if (!System.IO.File.Exists(filename))
                {
                    using (StreamWriter sw = System.IO.File.CreateText(filename))
                    {
                        sw.WriteLine(DateTime.Now + ": " + Message);
                    }
                }
                else
                {
                    using (StreamWriter sw = System.IO.File.AppendText(filename))
                    {
                        sw.WriteLine(DateTime.Now + ": " + Message);
                    }
                }

            }
        }

        // Лог записи результатов в CGM
        static void FileResultLog(string Message)
        {
            try
            {
                lock (FileResultLogLocker)
                {
                    //string path = AppDomain.CurrentDomain.BaseDirectory + "\\Log\\FileResult" + "\\" + DateTime.Now.Year + "\\" + DateTime.Now.Month;
                    string path = AppDomain.CurrentDomain.BaseDirectory + "\\Log\\FileResult";
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }

                    //string filename = path + $"\\{FileName}" + ".txt";
                    string filename = path + $"\\ResultLog_" + DateTime.Now.Date.ToShortDateString().Replace('/', '_') + ".txt";

                    if (!System.IO.File.Exists(filename))
                    {
                        using (StreamWriter sw = System.IO.File.CreateText(filename))
                        {
                            sw.WriteLine(DateTime.Now + ": " + Message);
                        }
                    }
                    else
                    {
                        using (StreamWriter sw = System.IO.File.AppendText(filename))
                        {
                            sw.WriteLine(DateTime.Now + ": " + Message);
                        }
                    }
                }
            }
            catch
            {

            }
        }

        // Лог драйвера
        static void ServiceLog(string Message)
        {
            lock (ServiceLogLocker)
            {
                try
                {
                    string path = AppDomain.CurrentDomain.BaseDirectory + "\\Log\\Service";
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }

                    string filename = path + "\\ServiceThread_" + DateTime.Now.Date.ToShortDateString().Replace('/', '_') + ".txt";
                    if (!System.IO.File.Exists(filename))
                    {
                        using (StreamWriter sw = System.IO.File.CreateText(filename))
                        {
                            sw.WriteLine(DateTime.Now + ": " + Message);
                        }
                    }
                    else
                    {
                        using (StreamWriter sw = System.IO.File.AppendText(filename))
                        {
                            sw.WriteLine(DateTime.Now + ": " + Message);
                        }
                    }
                }
                catch
                {

                }
            }
        }

        #endregion

        #region функции

        // Преобразование байтов в строку
        static string TranslateBytes(byte BytePar)
        {
            switch (BytePar)
            {
                case 0x02:
                    return "<STX>";
                case 0x03:
                    return "<ETX>";
                case 0x04:
                    return "<EOT>";
                case 0x05:
                    return "<ENQ>";
                case 0x06:
                    return "<ACK>";
                case 0x15:
                    return "<NAK>";
                case 0x16:
                    return "<SYN>";
                case 0x17:
                    return "<ETB>";
                case 0x0A:
                    return "<LF>";
                case 0x0D:
                    return "<CR>";
                default:
                    return "<HZ>";
            }
        }

        // Интерпретация результата
        static int ResultInterpretation(string result)
        {
            switch (result)
            {
                case "POSITIVE":
                    return 3113;
                // Код МО "Микроорганизмы не обнаружены"
                default:
                    return 89;
            }
        }

        // Для удобства чтения логов, делаем из байт строку и заменяем в ней управляющие байты на символы UTF8. Иначе в строке будут нечитаемые символы.
        public static string GetStringFromBytes(byte[] ReceivedDataPar)
        {
            byte[] BytesForCHecking = { 0x02, 0x03, 0x04, 0x05, 0x06, 0x15, 0x16, 0x17, 0x0D, 0x0A };
            int StepCount = 0; // позиция обнаруженного байта
            bool IsManageByte = false;
            Encoding utf8 = Encoding.UTF8;

            // проверяем, является ли байт в массиве управляющим байтом
            foreach (byte rec_byte in ReceivedDataPar)
            {
                foreach (byte check_byte in BytesForCHecking)
                {
                    if (rec_byte == check_byte)
                    {
                        IsManageByte = true;
                        break;
                    }
                }
                if (IsManageByte)
                { 
                    break;
                };
                StepCount++;
            }

            // Если обнаружен управляющий байт 
            if (IsManageByte)
            {   
                // объявляем новый массив, в который будет записаны все оставшиеся байты, начиная со следующей позиции после обнаруженного  
                byte[] SliceByteArray = new byte[ReceivedDataPar.Length - (StepCount + 1)];

                //(из какого массива, с какого индекса, в какой массив, с какого индекса, кол-во элементов)
                Array.Copy(ReceivedDataPar, StepCount + 1, SliceByteArray, 0, ReceivedDataPar.Length - (StepCount + 1));

                // возвращаем преобразованную строку
                return utf8.GetString(ReceivedDataPar, 0, StepCount)
                    + TranslateBytes(ReceivedDataPar[StepCount])
                    + GetStringFromBytes(SliceByteArray);
            }
            else
            {
               return utf8.GetString(ReceivedDataPar, 0, ReceivedDataPar.Length);
            }
        }

        //дописываем к номеру месяца ноль если нужно
        public static string CheckZero(int CheckPar)
        {
            string BackPar = "";
            if (CheckPar < 10)
            {
                BackPar = $"0{CheckPar}";
            }
            else
            {
                BackPar = $"{CheckPar}";
            }
            return BackPar;
        }

        // Создание файлов с результатами, которые будут разбираться
        static void MakeAnalyzerResultFile(string AllMessagePar)
        {
            if (!Directory.Exists(AnalyzerResultPath))
            {
                Directory.CreateDirectory(AnalyzerResultPath);
            }

            DateTime now = DateTime.Now;
            string filename = AnalyzerResultPath + "\\Results_" + now.Year + CheckZero(now.Month) + CheckZero(now.Day) + CheckZero(now.Hour) + CheckZero(now.Minute) + CheckZero(now.Second) + CheckZero(now.Millisecond) + ".res";
            
            using (FileStream fstream = new FileStream(filename, FileMode.OpenOrCreate))
            {
                foreach (string res in AllMessagePar.Split('\r'))
                {
                    Encoding utf8 = Encoding.UTF8;
                    byte[] ResByte = utf8.GetBytes(res + "\r\n");
                    fstream.Write(ResByte, 0, ResByte.Length);
                }
            }
        }
        #endregion

        #region Телеграм-бот для рассылки уведомлений
        // Функции, кроме отправки сообщения, нужны для того, чтобы получать информацию о пользователях, которые взаимодействовали с ботом
        // Когда пользователь отправляет сообщение, вызывается метод HandleUpdateAsync с объектом обновления Update, переданным в качестве аргумента
        public static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            
            // информация о пользователе и сообщении
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(update));

            if (update.Type == Telegram.Bot.Types.Enums.UpdateType.Message)
            {
                var message = update.Message;

                if (message.Text.ToLower() == "/start")
                {
                    await botClient.SendTextMessageAsync(message.Chat, "Бот анализатора Bactec FX запущен.");
                    Console.WriteLine(message.Text);
                    Console.WriteLine(message.Chat.Id);
                    Console.WriteLine(message.From.FirstName + " " + message.From.LastName);
                    string name = (message.From.FirstName + " " + message.From.LastName);
                    long chatId = message.Chat.Id;
                    AddAppSettings(name, chatId.ToString());
                    return;
                }

                await botClient.SendTextMessageAsync(message.Chat, "Бот анализатора Bactec FX работает.");
            }
            
        }

        public static async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var ErrorMessage = exception;
            Console.WriteLine(ErrorMessage);
        }

        // добавление пользователей, запустивших бот, в файл конфигурации
        // Нужно для рассылки всем пользщователям, которые запускали бот
        // Неактуально, т.к. реализовали через групповой чат
        static void AddAppSettings(string key, string value)
        {
            try
            {
                var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                var settings = configFile.AppSettings.Settings;
                // если такого ключа нет, то добавить
                if (settings[key] == null)
                {
                    settings.Add(key, value);
                }
                // если есть - перезаписать значение
                else
                {
                    //settings[key].Value = value;
                }
                configFile.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection(configFile.AppSettings.SectionInformation.Name);
            }
            catch (ConfigurationErrorsException)
            {
                Console.WriteLine("Error writing app settings");
            }
        }

        // отправка уведомлений пользователям
        public static async Task SendNotification(ITelegramBotClient botClient, string chat, string rid, string pid, string client, string fio)
        {
            //int chat_ = Int32.Parse(chat);
            // id канала
            long chat_ = Int64.Parse(chat);

            //string messageText = "Положительный флакон №: " + rid;
            string messageText = "Положительный флакон №: " + rid + "\n" + "Пациент: " + fio + "\n" + "Id пациента: " + pid + "\n" + "Код отделения: " + client;

            // Echo received message text
            Message sentMessage = await botClient.SendTextMessageAsync(
                                                                        chat_,
                                                                        messageText
                                                                        );
        }

        #endregion

        // Поток для проверки потоков чтения порта и обработки файлов с результатами
        public static void CheckThreads()
        {
            while (ServiceIsActive)
            {
                Thread.Sleep(60000);

                List<Thread> ListOfThreadsSearch = new List<Thread>();
                foreach (Thread th in ListOfThreads)
                {
                    ListOfThreadsSearch.Add(th);
                }
                foreach (Thread th in ListOfThreadsSearch)
                {
                    if (!th.IsAlive)
                    {
                        ServiceLog($"The thread {th.Name} is fucking dead");
                        try
                        {
                            if (th.Name == "ReadCOM")
                            {
                                ListOfThreads.Remove(th);
                                Thread NewThread = new Thread(ReadFromCOM);
                                NewThread.Name = th.Name;
                                ListOfThreads.Add(NewThread);
                                NewThread.Start();
                            }
                            if (th.Name == "ResultsProcessing")
                            {
                                ListOfThreads.Remove(th);
                                Thread NewThread = new Thread(ResultsProcessing);
                                NewThread.Name = th.Name;
                                ListOfThreads.Add(NewThread);
                                NewThread.Start();
                            }
                        }
                        catch (Exception e)
                        {
                            ServiceLog($"Can not start thread {th.Name}: {e}");
                        }
                    }
                    else
                    {
                        ServiceLog($"Thread {th.Name} is working");
                    }
                }
                ListOfThreadsSearch.Clear();
            }
        }

        // Регистрация заявки в CGM
        public static void RegistrationInCGM(string RID, SqlConnection CGMConnection) 
        {
            //Сначала получаем необходимые данные

            #region данные из таблицы autolid
            // получение данных из таблицы autolid
            string aut_senast ="";
            string aut_stopp; // максимальное значение счетчика
            int aut_aktuallitet = 0;

            SqlCommand GetFromAutolid = new SqlCommand("SELECT a.aut_stopp, a.aut_senast, a.aut_aktualitet FROM autolid a WHERE a.aut_typ = 'SECTION'", CGMConnection);
            SqlDataReader AutolidReader = GetFromAutolid.ExecuteReader();

            if (AutolidReader.HasRows)
            {
                while (AutolidReader.Read())
                {
                    aut_stopp = AutolidReader.GetString(0);
                    int autstopp = Convert.ToInt32(aut_stopp);

                    aut_senast = AutolidReader.GetString(1);
                    aut_aktuallitet = AutolidReader.GetInt32(2);

                    // преобразование счетчика из aut_senast из str в int
                    int aut_senast_counter = Convert.ToInt32(aut_senast);
                    aut_senast_counter++; // инкремент счетчика

                    if (aut_senast_counter <= autstopp)
                    {
                        aut_senast = aut_senast_counter.ToString();
                        if (aut_senast.Length < aut_stopp.Length)
                        {
                            // формируем счетчик определенной длины (6) с учетом нулей 
                            while (aut_senast.Length != aut_stopp.Length)
                            {
                                aut_senast = "0" + aut_senast;
                            }
                        }
                    }

                }

            }
            AutolidReader.Close();

            #endregion

            #region данные из таблицы identitet
            int id_senast = 0;
            int id_aktualitet = 0;

            SqlCommand GetFromIdentitet = new SqlCommand("SELECT i.id_senast, i.id_aktualitet FROM identitet i WHERE i.id_namn = 'prv_id'", CGMConnection);
            SqlDataReader IdentitetReader = GetFromIdentitet.ExecuteReader();

            if (IdentitetReader.HasRows)
            {
                while (IdentitetReader.Read())
                {
                    id_senast = IdentitetReader.GetInt32(0);
                    id_aktualitet = IdentitetReader.GetInt32(1);
                }
            }
            IdentitetReader.Close();

            #endregion

            #region данные из searchview

            // Данные для последующих апдейтов
            int rem_id = 0;
            int pro_id = 0;
            string TestCode = "";
            string adr_kod = "";

            SqlCommand GetFromSearchview = new SqlCommand(
                    "SELECT s.rem_id, s.pro_id, s.ana_analyskod, s.prov_adr_kod_regvid FROM LABETT..searchview s " +
                    "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                    "WHERE s.rem_rid = @rid AND s.bes_ank_dttm IS NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMConnection);

            GetFromSearchview.Parameters.Add(new SqlParameter("@rid", RID));
            SqlDataReader GetFromSearchviewReader = GetFromSearchview.ExecuteReader();

            if (GetFromSearchviewReader.HasRows)
            {
                while (GetFromSearchviewReader.Read())
                {
                    if (!GetFromSearchviewReader.IsDBNull(0))
                    {
                        rem_id = GetFromSearchviewReader.GetInt32(0);
                    }
                    if (!GetFromSearchviewReader.IsDBNull(1))
                    {
                        pro_id = GetFromSearchviewReader.GetInt32(1);
                    }
                    if (!GetFromSearchviewReader.IsDBNull(2))
                    {
                        TestCode = GetFromSearchviewReader.GetString(2);
                    }
                    if (!GetFromSearchviewReader.IsDBNull(3))
                    {
                        adr_kod = GetFromSearchviewReader.GetString(3);
                    }
                }
            }
            GetFromSearchviewReader.Close();
            #endregion

            // В двух блоках ниже - данные для инсерта в provnr, счетчик YYSSSNNNNNN
            #region данные из таблицы provnr

            int prv_id = 0; // счетчик
            string year;    // переменная текущего года, для формирования счетчика prv_prvnr

            SqlCommand GetFromProvnr = new SqlCommand("SELECT MAX(p.prv_id) FROM provnr p", CGMConnection);
            SqlDataReader ProvnrReader = GetFromProvnr.ExecuteReader();

            if (ProvnrReader.HasRows)
            {
                while (ProvnrReader.Read())
                {
                    prv_id = ProvnrReader.GetInt32(0);
                }
            }
            ProvnrReader.Close();

            #endregion

            #region данные из таблицы metod, получение кода секции для теста, формирование счетчика для таблицы provnr

            string sek_kod = "";
            int meg_id = 0;         // id, по которому определяем, какие среды должны быть добавлены 
            string prv_provnr = ""; //счетчик prv_provnr таблицы provnr

            SqlCommand GetFromMetod = new SqlCommand("SELECT m.sek_kod, m.meg_id FROM metod m WHERE m.ana_analyskod = @test_code", CGMConnection);
            GetFromMetod.Parameters.Add(new SqlParameter("@test_code", TestCode));
            SqlDataReader MetodReader = GetFromMetod.ExecuteReader();

            if (MetodReader.HasRows)
            {
                while (MetodReader.Read())
                {
                    sek_kod = MetodReader.GetString(0);
                    meg_id = MetodReader.GetInt32(1);
                }
            }
            MetodReader.Close();
            prv_provnr = DateTime.Now.ToString("yy") + sek_kod + aut_senast;

            #endregion

            #region данные из таблицы ana, saving time
            string ana_spartid = ""; // saving time

            SqlCommand GetFromAna = new SqlCommand("SELECT a.ana_spartid FROM ana a WHERE a.ana_analyskod = @test_code", CGMConnection);
            GetFromAna.Parameters.Add(new SqlParameter("@test_code", TestCode));
            SqlDataReader AnaReader = GetFromAna.ExecuteReader();

            if (AnaReader.HasRows)
            {
                while (AnaReader.Read())
                {
                    ana_spartid = AnaReader.GetString(0);
                }                
            }
            AnaReader.Close();
            #endregion

            #region данные из таблицы mediagroup_media, формируем словарь со средами, которые нужно будет добавить в таблицу plate

            //Dictionary<string, int> culture_plate = new Dictionary<string, int>();
            var culture_plate = new Dictionary<string, int>();

            SqlCommand GetFromMediagroup_media = new SqlCommand("SELECT mm.mea_code, mm.mem_sort_order FROM mediagroup_media mm WHERE mm.meg_id = @meg_id", CGMConnection);
            GetFromMediagroup_media.Parameters.Add(new SqlParameter("@meg_id", meg_id));
            SqlDataReader Mediagroup_mediaReader = GetFromMediagroup_media.ExecuteReader();

            string mea_code = "";
            int sort_order = 0;

            if (Mediagroup_mediaReader.HasRows)
            {
                while (Mediagroup_mediaReader.Read())
                {
                    mea_code = Mediagroup_mediaReader.GetString(0);
                    sort_order = Mediagroup_mediaReader.GetInt16(1);
                    culture_plate.Add(mea_code, sort_order);
                }
            }
            Mediagroup_mediaReader.Close();
         
            #endregion
            
            #region регистрация заявки в CGM, с учетом полученных значений
            // начало транзакции
            SqlTransaction RequestRegistrationTransaction = CGMConnection.BeginTransaction();

            // обновление таблицы autolid
            SqlCommand UpdateAutolid = CGMConnection.CreateCommand();
            UpdateAutolid.CommandText = "UPDATE dbo.autolid " +
                                            "SET aut_aktualitet = @aut_aktuallitet + 1, aut_chg_time = GETDATE(), aut_chg_user = 'ADMIN', aut_senast = @aut_senast " +
                                            "WHERE aut_typ = 'SECTION' AND aut_aktualitet = @aut_aktuallitet";
            UpdateAutolid.Parameters.Add(new SqlParameter("@aut_aktuallitet", aut_aktuallitet));
            UpdateAutolid.Parameters.Add(new SqlParameter("@aut_senast", aut_senast));
            UpdateAutolid.Transaction = RequestRegistrationTransaction;
            

            // обновление таблицы identitet
            SqlCommand UpdateIdentitet = CGMConnection.CreateCommand();
            UpdateIdentitet.CommandText = "UPDATE identitet " +
                                            "WITH(UPDLOCK, ROWLOCK) " +
                                            "SET id_senast = @id_senast + 1, id_chg_time = GETDATE(), id_chg_user = 'SCRIPT', id_aktualitet = @id_aktualitet + 1 " +
                                          "WHERE id_namn = 'prv_id' and id_aktualitet = @id_aktualitet";
            UpdateIdentitet.Parameters.Add(new SqlParameter("@id_senast", id_senast));
            UpdateIdentitet.Parameters.Add(new SqlParameter("@id_aktualitet", id_aktualitet));
            UpdateIdentitet.Transaction = RequestRegistrationTransaction;

            // обновление таблицы remiss
            SqlCommand UpdateRemiss = CGMConnection.CreateCommand();
            UpdateRemiss.CommandText = "UPDATE remiss " +
                                            "SET rem_rid = @rid, adr_kod_ankreg = '41', rem_ank_dttm = GETDATE(), " +
                                            "rem_ankstatus = 'Z', adr_kod_ragare = '41', rem_debdat = GETDATE(), " +
                                            "rem_chg_time = GETDATE(), rem_chg_user = 'dbo', rem_aktualitet = '1' " +
                                         "WHERE rem_id = @rem_id and rem_aktualitet = '0'";
            UpdateRemiss.Parameters.Add(new SqlParameter("@rid", RID));
            UpdateRemiss.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            UpdateRemiss.Transaction = RequestRegistrationTransaction;

            // обновление таблицы prov
            SqlCommand UpdateProv = CGMConnection.CreateCommand();
            UpdateProv.CommandText = "UPDATE prov " +
                                        "SET sig_sign_ankomstreg = 'BACTEC', pro_ankomst_dttm = GETDATE(), adr_kod_ankomstlab = '41', " +
                                        "pro_chg_time = GETDATE(), pro_chg_user = 'dbo', pro_aktualitet = '1' " +
                                      "WHERE pro_id = @pro_id and pro_aktualitet = '0'";
            UpdateProv.Parameters.Add(new SqlParameter("@pro_id", pro_id));
            UpdateProv.Transaction = RequestRegistrationTransaction;

            // добавление данных в таблицу provnr
            SqlCommand InsertProvnr = CGMConnection.CreateCommand();
            InsertProvnr.CommandText = "INSERT INTO provnr ( prv_id, prv_provnr, prv_crt_time, prv_crt_user ) " +
                                       "VALUES (@prv_id, @prv_provnr, GETDATE(), 'dbo')";
            InsertProvnr.Parameters.Add(new SqlParameter("@prv_id", prv_id + 1));
            InsertProvnr.Parameters.Add(new SqlParameter("@prv_provnr", prv_provnr));
            InsertProvnr.Transaction = RequestRegistrationTransaction;

            // обновление таблицы bestall
            SqlCommand UpdateBestall = CGMConnection.CreateCommand();
            UpdateBestall.CommandText = "UPDATE bestall " +
                                            "SET sig_sign_anksign = 'BACTEC', bes_ank_dttm = GETDATE(), adr_kod_ankreg = '41', " +
                                            "bes_spartid = DATEADD(day, @saving_time, CAST(GETDATE() AS DATE)), adr_kod_bagare = '41', prv_id = @prv_id, " +
                                            "bes_chg_time = GETDATE(), bes_chg_user = 'dbo', bes_aktualitet = '1' " +
                                        "WHERE pro_id = @pro_id and rem_id = @rem_id and ana_analyskod = @test_code and bes_aktualitet = '0'";
            UpdateBestall.Parameters.Add(new SqlParameter("@saving_time", Int32.Parse(ana_spartid)));
            UpdateBestall.Parameters.Add(new SqlParameter("@prv_id", prv_id));
            UpdateBestall.Parameters.Add(new SqlParameter("@pro_id", pro_id));
            UpdateBestall.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            UpdateBestall.Parameters.Add(new SqlParameter("@test_code", TestCode));
            UpdateBestall.Transaction = RequestRegistrationTransaction;

            //  удаление из таблицы reportreceiver
            SqlCommand DeleteReportreceiver_1 = CGMConnection.CreateCommand();
            DeleteReportreceiver_1.CommandText = "DELETE FROM reportreceiver where rem_id=@rem_id and adr_kod=@client";
            DeleteReportreceiver_1.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            DeleteReportreceiver_1.Parameters.Add(new SqlParameter("@client", adr_kod));
            DeleteReportreceiver_1.Transaction = RequestRegistrationTransaction;

            SqlCommand DeleteReportreceiver_2 = CGMConnection.CreateCommand();
            DeleteReportreceiver_2.CommandText = "DELETE FROM reportreceiver where rem_id=@rem_id and adr_kod='MICROB2'";
            DeleteReportreceiver_2.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            DeleteReportreceiver_2.Transaction = RequestRegistrationTransaction;

            // добавление данных в таблицу reportreceiver
            SqlCommand InsertReportreceiver = CGMConnection.CreateCommand();
            InsertReportreceiver.CommandText = "INSERT INTO reportreceiver ( rem_id, adr_kod, phy_id, repr_type, sig_sign, repr_crt_user ) " +
                                               "VALUES(@rem_id, @client, NULL, 'F', 'BACTEC', 'dbo')";
            InsertReportreceiver.Parameters.Add(new SqlParameter("@rem_id", rem_id));
            InsertReportreceiver.Parameters.Add(new SqlParameter("@client", adr_kod));
            InsertReportreceiver.Transaction = RequestRegistrationTransaction;

            // добавление данных в таблицу plate
            SqlCommand InsertPlate = CGMConnection.CreateCommand();
            InsertPlate.CommandText = "INSERT INTO plate ( " +
                                            "rem_id, pro_id, ana_analyskod, " +
                                            "mea_code, pla_fin_id_count, sig_sign, " +
                                            "pla_crt_user, pla_crt_time, pla_chg_user, " +
                                            "pla_chg_time, pla_chg_version, pla_media_sort_order ) " +
                                         "VALUES (" +
                                            "@rem_id, @pro_id, @testcode, " +
                                            "@mea, '0', 'BACTEC', " +
                                            "'dbo', GETDATE(), 'dbo', " +
                                            "GETDATE(), '0', @sort_order)";
            InsertPlate.Transaction = RequestRegistrationTransaction;

            // выполнение скриптов
            try
            {
                UpdateAutolid.ExecuteNonQuery();
                UpdateIdentitet.ExecuteNonQuery();
                UpdateRemiss.ExecuteNonQuery();
                UpdateProv.ExecuteNonQuery();
                InsertProvnr.ExecuteNonQuery();
                UpdateBestall.ExecuteNonQuery();
                DeleteReportreceiver_1.ExecuteNonQuery();
                DeleteReportreceiver_2.ExecuteNonQuery();
                InsertReportreceiver.ExecuteNonQuery();

                foreach (var plate in culture_plate)
                {
                    string meacode = plate.Key;
                    int sortorder = plate.Value;
                    InsertPlate.Parameters.Clear();
                    InsertPlate.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                    InsertPlate.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                    InsertPlate.Parameters.Add(new SqlParameter("@testcode", TestCode));
                    InsertPlate.Parameters.Add(new SqlParameter("@mea", meacode));
                    InsertPlate.Parameters.Add(new SqlParameter("@sort_order", sortorder));
                    InsertPlate.ExecuteNonQuery();
                }

                // завершение операций после выполнения
                RequestRegistrationTransaction.Commit();

                // запись в лог
                FileResultLog($"Request {RID} is registered in CGM.");
            }
            catch (Exception ex)
            {
                RequestRegistrationTransaction.Rollback();
                FileResultLog($"{ex}");
                //FileResultLog($"Result {InsertResult} is NOT inserted!");
                FileResultLog($"");
            }
            #endregion
        }

        // Запись результатов в CGM
        public static void InsertResultToCGM(string InsertRid, string InsertResult)
        {
            int ResultForInsert = ResultInterpretation(InsertResult); // интерпретация результата
            //bool RIDExist = false;
            bool IsCultureTest = false; // флаг посевного теста
            FileToErrorPath = false;        // флаг 

            try
            {
                //string CGMConnectionString = @"Data Source=CGM-DATA02; Initial Catalog=LABETT; Integrated Security=True; User Id = PSMExchangeUser; Password = PSM_123456";

                //string CGMConnectionString = @"Data Source=CGM-DATA02; Initial Catalog=LABETT; Integrated Security=True;";
                //string CGMConnectionString = @"Data Source = CGM-DATA01\CGMSQL; Initial Catalog = LABETT;";

                string CGMConnectionString = ConfigurationManager.ConnectionStrings["CGMConnection"].ConnectionString;
                CGMConnectionString = String.Concat(CGMConnectionString, $"User Id = {user}; Password = {password}"); 
               
                using (SqlConnection CGMconnection = new SqlConnection(CGMConnectionString))
                {
                    CGMconnection.Open();

                    #region Проверяем RID

                    int TestCount = 0; // переменная для подсчета кол-ва зарегистрированных микробиологических тестов

                    // Проверяем RID. Микробиологический тест должен быть в заявке, быть зарегистрирован и не подтвержден.
                    SqlCommand RIDExistCommand = new SqlCommand(
                                "SELECT s.rem_rid, s.ana_analyskod, s.bes_ank_dttm, s.bes_reg_dttm, s.bes_t_dttm, a.dis_kod " +
                                "FROM LABETT..searchview s " +
                                "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod" +
                                $" WHERE s.rem_rid = '{InsertRid}' AND s.bes_ank_dttm IS NOT NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);
                    
                    SqlDataReader Reader = RIDExistCommand.ExecuteReader();

                    // Если такой (такие) тест(ы) есть, то продолжаем работу
                    if (Reader.HasRows)
                    {
                        IsCultureTest = true;
                        FileResultLog($"Request {InsertRid} with culture test is registered, test is not validated.");
                        Reader.Close();
                    }
                    else
                    {
                        Reader.Close();
                        //Запись в лог , что такая заявка не зарегана в CGM
                        FileResultLog($"Request {InsertRid} with culture test is NOT registered in CGM.");

                        int CulTestcount = 0;   // переменная для подсчета кол-ва незарегистрированных микробиологических тестов в заявке
                        bool IsСulTest = false; // флаг для определения, есть ли посевные тесты в заявке

                        // Проверяем, есть ли в принципе микробиологические тесты в заявке (и в принципе заявка)
                        // Если они есть и не зарегистрированы, то нужно посчитать кол-во тестов, если тест один - зарегистрировать его.
                        SqlCommand CultureTestInRequestCommand = new SqlCommand(
                            "SELECT s.rem_rid, s.ana_analyskod, s.bes_ank_dttm, s.bes_reg_dttm, s.bes_t_dttm, a.dis_kod " +
                            "FROM LABETT..searchview s INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                            "WHERE s.rem_rid = @rid AND s.bes_reg_dttm IS NOT NULL AND s.bes_ank_dttm IS NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);

                        CultureTestInRequestCommand.Parameters.Add(new SqlParameter("@rid", InsertRid));
                        SqlDataReader CultureTestInRequestReader = CultureTestInRequestCommand.ExecuteReader();

                        if (CultureTestInRequestReader.HasRows)
                        {
                            IsСulTest = true; //В заявке есть микробиологические тесты
                        }
                        else
                        {
                            FileResultLog($"Request {InsertRid} does not contain culture tests");
                            FileToErrorPath = true; //флаг указывает на то, что файл будет перемещен в папку с ошибками
                        }
                        CultureTestInRequestReader.Close();
                        //FileToErrorPath = true; //флаг указывает на то, что файл будет перемещен в папку с ошибками

                        // Если в заявке есть микробилоогические тесты, нужно посчитать кол-во, регистрируется только один тест
                        if (IsСulTest)
                        {
                            SqlCommand CulTestCountCommand = new SqlCommand(
                                "SELECT COUNT(*) FROM LABETT..searchview s " +
                                "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                                "WHERE s.rem_rid = @rid AND s.bes_ank_dttm IS NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);
                            CulTestCountCommand.Parameters.Add(new SqlParameter("@rid", InsertRid));
                            SqlDataReader CulTestCountReader = CulTestCountCommand.ExecuteReader();

                            if (CulTestCountReader.HasRows)
                            {
                                while (CulTestCountReader.Read())
                                {
                                    CulTestcount = CulTestCountReader.GetInt32(0);
                                }
                            }
                            CulTestCountReader.Close();

                            // Если тест один
                            if (CulTestcount == 1)
                            {
                                FileResultLog($"Request {InsertRid} exists and need to be registered.");
                                // функция регистрации заявки
                                RegistrationInCGM(InsertRid, CGMconnection);
                                // флаг того, что в заявке есть зарегистрированный микробиологический тест, чтобы продолжить выполнение после регистрации
                                IsCultureTest = true;
                            }
                            else 
                            {
                                FileResultLog($"Request {InsertRid} contains more than one culture test. Unable to register.");
                            }
                        }
                    }
                    //Reader.Close();

                    // Если зарегистрирован микробиологический тест
                    if (IsCultureTest)
                    {   
                        // Проверяем, один ли посевный тест зарегистрирован в заявке
                        SqlCommand CultureTestCount = new SqlCommand(
                            "SELECT COUNT(*) FROM LABETT..searchview s " +
                            "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                            $"WHERE s.rem_rid = '{InsertRid}' AND s.bes_ank_dttm IS NOT NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);

                        Reader = CultureTestCount.ExecuteReader();

                       // int TestCount = 0;
                        if (Reader.HasRows)
                        {
                            while (Reader.Read())
                            {
                                TestCount = Reader.GetInt32(0);
                            }
                            FileResultLog($"{TestCount} culture test in the request.");
                        }
                        Reader.Close();
                    }
                    #endregion

                    // Если зарегистрирован микробиологический тест и кол-во тестов в заявке = 1
                    if (IsCultureTest && TestCount == 1)
                    {
                        #region pro_id, rem_id, тест, lid
                        // находим pro_id, rem_id, тест, lid
                        int rem_id = 0;
                        int pro_id = 0;
                        string TestCode = "";
                        string Lid = "";

                        //string pid = "";

                        SqlCommand GetData = new SqlCommand(
                            "SELECT s.rem_id, s.pro_id, s.ana_analyskod, s.pro_provid, s.pop_pid FROM LABETT..searchview s " +
                            "INNER JOIN ana a ON s.ana_analyskod = a.ana_analyskod " +
                            $"WHERE s.rem_rid = '{InsertRid}' AND s.bes_ank_dttm IS NOT NULL AND s.bes_t_dttm IS NULL AND s.ana_analyskod LIKE 'P_%' AND a.dis_kod = 'Б'", CGMconnection);

                        SqlDataReader DataReader = GetData.ExecuteReader();

                        if (DataReader.HasRows)
                        {
                            while (DataReader.Read())
                            {
                                rem_id = DataReader.GetInt32(0);
                                pro_id = DataReader.GetInt32(1);
                                TestCode = DataReader.GetString(2);
                                Lid = DataReader.GetString(3);
                                //pid = DataReader.GetString(4);
                            }
                        }
                        DataReader.Close();
                        #endregion

                        #region Проверка среды

                        // проверяем, есть ли среда Bactec, если нет - добавляем
                        bool IsMediaExist = false; // флаг наличия среды 

                        SqlCommand GetMedia = new SqlCommand($"SELECT * FROM LABETT..plate p WHERE p.rem_id = {rem_id} AND p.mea_code = 'BT_BLOOD'", CGMconnection);
                        SqlDataReader MediaReader = GetMedia.ExecuteReader();

                        if (MediaReader.HasRows)
                        {
                            IsMediaExist = true;
                            //Console.WriteLine("Среда Бактек есть в заявке");
                            //FileResultLog($"Среда BACTEC есть в заявке");
                            MediaReader.Close();
                        }
                        else
                        {
                            MediaReader.Close();
                            SqlTransaction InsertMediaBactec = CGMconnection.BeginTransaction();
                            SqlCommand InsertBactec = CGMconnection.CreateCommand();

                            InsertBactec.Transaction = InsertMediaBactec;

                            InsertBactec.CommandText = $"INSERT INTO LABETT..plate VALUES ({rem_id}, {pro_id}, '{TestCode}', 'BT_BLOOD', 'SCRIPT', 'dbo', GETDATE(), 0, 'dbo', GETDATE(), 1, 2)";
                            InsertBactec.ExecuteNonQuery();

                            InsertMediaBactec.Commit();
                            IsMediaExist = true;

                            //Console.WriteLine("Среда Бактек добавлена в заявку");
                            FileResultLog($"BACTEC media is inserted.");
                        }
                        #endregion

                        if (IsMediaExist)
                        {
                            #region Кол-во микроорганизмов в среде BACTEC

                            int finCount = 0;

                            SqlCommand GetMOCount = new SqlCommand($"SELECT COUNT(*) FROM LABETT..finding f WHERE f.rem_id = {rem_id} AND f.mea_code = 'BT_BLOOD'", CGMconnection);
                            SqlDataReader MOCountReader = GetMOCount.ExecuteReader();

                            if (MOCountReader.HasRows)
                            {
                                while (MOCountReader.Read())
                                {
                                    finCount = MOCountReader.GetInt32(0);
                                    //Console.WriteLine($"{finCount} микроорганизма в среде"); ;
                                }
                            }
                            MOCountReader.Close();

                            #endregion

                            #region Проверяем есть ли такие микроорганизмы в среде
                            SqlCommand CheckMO = new SqlCommand(
                                        "SELECT fin_id from  finding where rem_id=@rem_id " +
                                        "and pro_id=@pro_id AND ana_analyskod = @test_code and mea_code='BT_BLOOD' and fyt_id=@fyt_id ", CGMconnection);
                            CheckMO.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                            CheckMO.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                            CheckMO.Parameters.Add(new SqlParameter("@test_code", TestCode));
                            CheckMO.Parameters.Add(new SqlParameter("@fyt_id", ResultForInsert));
                            SqlDataReader CheckMOReader = CheckMO.ExecuteReader();

                            bool Is_There_MO = CheckMOReader.HasRows;
                            CheckMOReader.Close();
                            #endregion
              
                            // Если микроорганизма нет в среде, то Insert
                            if (!Is_There_MO)
                            {
                                FileResultLog("Getting data from tables prov, plate, findings");

                                #region Получение данных из таблицы prov

                                int pro_fin_count = 0;
                                int pro_aktualitet = 0;

                                SqlCommand GetProvData = new SqlCommand("SELECT pro_fin_count, pro_aktualitet FROM prov WHERE pro_id = @pro_id ", CGMconnection);
                                GetProvData.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                SqlDataReader ProvReader = GetProvData.ExecuteReader();
                                if (ProvReader.HasRows)
                                {
                                    while (ProvReader.Read())
                                    {
                                        if (!ProvReader.IsDBNull(0))
                                        {
                                            //pro_fin_count = ProvReader.GetInt32(0);
                                            pro_fin_count = ProvReader.GetInt16(0);
                                        }
                                        if (!ProvReader.IsDBNull(1))
                                        {
                                            pro_aktualitet = ProvReader.GetInt16(1);
                                        }
                                    }
                                }
                                ProvReader.Close();

                                #endregion

                                #region Получение данных из таблицы plate
                                int fin_id_count = 0;
                                int chg_version = 0;
                                SqlCommand GetPlateData = new SqlCommand("SELECT pla_fin_id_count, pla_chg_version FROM LABETT..plate p WHERE p.rem_id = @rem_id" +
                                    " and pro_id = @pro_id AND ana_analyskod = @test_code AND p.mea_code = 'BT_BLOOD'", CGMconnection);
                                GetPlateData.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                GetPlateData.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                GetPlateData.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                SqlDataReader PlateReader = GetPlateData.ExecuteReader();
                                if (PlateReader.HasRows)
                                {
                                    while (PlateReader.Read())
                                    {
                                        if (!PlateReader.IsDBNull(0))
                                        {
                                            fin_id_count = PlateReader.GetInt16(0);
                                        }
                                        if (!PlateReader.IsDBNull(1))
                                        {
                                            chg_version = PlateReader.GetInt16(1);
                                        }
                                    }
                                }
                                PlateReader.Close();

                                // инкремент счетчиков
                                fin_id_count++;
                                chg_version++;

                                // определяем значение в столбце fin_number, сумма значений pla_fin_id_count
                                int fin_number = 0;
                                SqlCommand fin_numberCount = new SqlCommand(
                                    "select SUM(pla_fin_id_count) from plate " +
                                    "where rem_id=@rem_id and pro_id=@pro_id and ana_analyskod = @test_code " +
                                    "GROUP BY rem_id ", CGMconnection);
                                fin_numberCount.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                fin_numberCount.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                fin_numberCount.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                SqlDataReader fin_numberCountReader = fin_numberCount.ExecuteReader();
                                if (fin_numberCountReader.HasRows)
                                {
                                    while (fin_numberCountReader.Read())
                                    {
                                        if (!fin_numberCountReader.IsDBNull(0))
                                        {
                                            fin_number = fin_numberCountReader.GetInt32(0);
                                        }
                                    }
                                }
                                fin_numberCountReader.Close();
                                fin_number++;
                                #endregion

                                #region Получение данных из таблицы finding
                                // Если уже какие-то микроорганизмы есть в среде, то получаем данные счетчиков из таблицы
                                // Если МО нет, то просто увеличиваем счетчик, для последующего insert
                                int fin_sort_order = 0;

                                SqlCommand GetFindingData = new SqlCommand("SELECT MAX(fin_sort_order) from finding " +
                                            "WHERE rem_id=@rem_id and pro_id=@pro_id ", CGMconnection);
                                GetFindingData.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                GetFindingData.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                SqlDataReader FindingReader = GetFindingData.ExecuteReader();
                                if (FindingReader.HasRows)
                                {
                                    while (FindingReader.Read())
                                    {
                                        if (!FindingReader.IsDBNull(0))
                                        {
                                            fin_sort_order = FindingReader.GetInt16(0);
                                        }
                                    }
                                }
                                FindingReader.Close();
                                fin_sort_order++;

                                #endregion

                                FileResultLog("Data from tables are received");

                                // Запись данных в БД CGM

                                FileResultLog("Inserting results...");

                                SqlTransaction InsertMOTransaction = CGMconnection.BeginTransaction();

                                #region Update таблицы prov
                                SqlCommand UpdateProv = CGMconnection.CreateCommand();
                                UpdateProv.CommandText = "UPDATE prov SET pro_fin_count = @pro_fin_count, pro_chg_time = GETDATE(), " +
                                                         "pro_aktualitet = @pro_aktualitet " +
                                                         "where pro_id = @pro_id and pro_aktualitet = @old_pro_aktualitet";
                                UpdateProv.Parameters.Add(new SqlParameter("@pro_fin_count", pro_fin_count + 1));
                                UpdateProv.Parameters.Add(new SqlParameter("@pro_aktualitet", pro_aktualitet + 1));
                                UpdateProv.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                UpdateProv.Parameters.Add(new SqlParameter("@old_pro_aktualitet", pro_aktualitet));
                                UpdateProv.Transaction = InsertMOTransaction;
                                #endregion

                                #region Update таблицы plate
                                // сначала апдейт основных данных в среде Bactec
                                SqlCommand UpdatePlate = CGMconnection.CreateCommand();
                                UpdatePlate.CommandText = "Update plate SET pla_fin_id_count = @fin_id_count, sig_sign = 'SCRIPT', " +
                                    "pla_chg_user = 'dbo', pla_chg_time = GETDATE(), pla_chg_version = @chg_version, pla_media_sort_order = '2' " +
                                    "where rem_id=@rem_id and pro_id = @pro_id and ana_analyskod=@test_code and mea_code='BT_BLOOD' ";
                                UpdatePlate.Parameters.Add(new SqlParameter("@fin_id_count", fin_id_count));
                                UpdatePlate.Parameters.Add(new SqlParameter("@chg_version", chg_version));
                                UpdatePlate.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                UpdatePlate.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                UpdatePlate.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                UpdatePlate.Transaction = InsertMOTransaction;

                                // апдейт chg_version для других сред
                                SqlCommand UpdatePlate_others = CGMconnection.CreateCommand();
                                UpdatePlate_others.CommandText = "UPDATE plate SET sig_sign = 'SCRIPT', pla_chg_user = 'dbo', pla_chg_time = GETDATE(), pla_chg_version = pla_chg_version+1 " +
                                    "where rem_id=@rem_id and pro_id= @pro_id and ana_analyskod=@test_code and mea_code <>'BT_BLOOD'";
                                UpdatePlate_others.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                UpdatePlate_others.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                UpdatePlate_others.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                UpdatePlate_others.Transaction = InsertMOTransaction;

                                #endregion

                                #region Insert МО в таблицу finding

                                SqlCommand InsertMO = CGMconnection.CreateCommand();
                                InsertMO.CommandText = "INSERT INTO finding ( " +
                                    "rem_id, pro_id, ana_analyskod, mea_code, fin_id, " +
                                    "fyt_id, fin_sort_order, amo_id, fin_fin_comment_int, fin_fin_comment_ext, " +
                                    "fin_res_comment, fin_origin, fin_reply, fin_include_amount_on_reports, sig_sign, " +
                                    "fin_analys_dttm, fin_number, fin_crt_user, fin_crt_time, fin_chg_user, " +
                                    "fin_chg_time, fin_chg_version, culture_performing_laboratory ) " +
                                    "VALUES ( " +
                                    "@rem_id, @pro_id, @test_code, 'BT_BLOOD', @fin_id_count, " +
                                    "@fyt_id, @fin_sort_order, NULL, NULL, NULL, " +
                                    "NULL, 'BACTEC', 'X', '1', 'SCRIPT', " +
                                    "GETDATE(), @fin_number, 'dbo', GETDATE(), 'dbo', " +
                                    "GETDATE(), '0', '41' )";
                                InsertMO.Parameters.Add(new SqlParameter("@rem_id", rem_id));
                                InsertMO.Parameters.Add(new SqlParameter("@pro_id", pro_id));
                                InsertMO.Parameters.Add(new SqlParameter("@test_code", TestCode));
                                InsertMO.Parameters.Add(new SqlParameter("@fin_id_count", fin_id_count));
                                InsertMO.Parameters.Add(new SqlParameter("@fyt_id", ResultForInsert));
                                InsertMO.Parameters.Add(new SqlParameter("@fin_sort_order", fin_sort_order));
                                InsertMO.Parameters.Add(new SqlParameter("@fin_number", fin_number));
                                InsertMO.Transaction = InsertMOTransaction;

                                #endregion

                                try
                                {
                                    UpdateProv.ExecuteNonQuery();
                                    UpdatePlate.ExecuteNonQuery();
                                    UpdatePlate_others.ExecuteNonQuery();
                                    InsertMO.ExecuteNonQuery();
                                    InsertMOTransaction.Commit();

                                    // запись в лог
                                    FileResultLog($"Result {InsertResult} is inserted.");
                                    FileResultLog($"");
                                }
                                catch(Exception ex)
                                {
                                    InsertMOTransaction.Rollback();
                                    //Console.WriteLine(ex);
                                    FileResultLog($"{ex}");
                                    FileResultLog($"Result {InsertResult} is NOT inserted!");
                                    FileResultLog($"");
                                }
                            }
                            // Если микроорганизм уже есть, то ничего не записываем
                            else
                            {
                                FileResultLog("Result is already exists.");
                                FileResultLog($"");
                            }
                        }

                    }
                    else
                    {
                        // невозможно записать данные в CGM
                        FileResultLog($"Impossible to insert data to CGM");
                        FileToErrorPath = true; //флаг указывает на то, что файл будет перемещен в папку с ошибками
                    }

                    CGMconnection.Close();
                }
            }
            catch (Exception Error)
            {
                FileResultLog($"{Error}");
            }
        }

        // Обработка файлов с результатами
        static void ResultsProcessing()
        {
            while (ServiceIsActive)
            {
                try
                {
                    if (!Directory.Exists(AnalyzerResultPath))
                    {
                        Directory.CreateDirectory(AnalyzerResultPath);
                    }

                    string ArchivePath = AnalyzerResultPath + @"\Archive";
                    string ErrorPath = AnalyzerResultPath + @"\Error";

                    if (!Directory.Exists(ArchivePath))
                    {
                        Directory.CreateDirectory(ArchivePath);
                    }

                    if (!Directory.Exists(ErrorPath))
                    {
                        Directory.CreateDirectory(ErrorPath);
                    }

                    string[] Files = Directory.GetFiles(AnalyzerResultPath, "*.res");

                    //string RIDPattern = @"[O][|][1][|](?<RID>\d+)[|]{2}";
                    // Ищет RID
                    //string RIDPattern = @"[O][|][1][|](?<RID>\d+)[|]{2}\S*";
                    // Ищет еще и LID
                    string RIDPattern = @"[O][|][1][|](?<RID>\w+)[|]{2}\S*";
                    //string ResultPattern = @"[R][|][1][|]\S+[|]INST[_](?<Result>\w+)[|]\S*";
                    string ResultPattern = @"[R][|][1][|]\S+INST_(?<Result>\w+)[|]";

                    Regex RIDRegex = new Regex(RIDPattern, RegexOptions.None, TimeSpan.FromMilliseconds(150));
                    Regex ResultRegex = new Regex(ResultPattern, RegexOptions.None, TimeSpan.FromMilliseconds(150));

                    foreach (string file in Files)
                    {
                        string[] lines = System.IO.File.ReadAllLines(file);
                        string RID = "";
                        string Result = "";

                        foreach (string line in lines)
                        {
                            //Console.WriteLine(line);
                            //FileResultLog(line);
                            Match RIDMatch = RIDRegex.Match(line);

                            if (RIDMatch.Success)
                            {
                                RID = RIDMatch.Result("${RID}");
                            }
                            else
                            {
                                //Console.WriteLine("FAIL");
                            }

                            Match ResultMatch = ResultRegex.Match(line);
                            if (ResultMatch.Success)
                            {
                                Result = ResultMatch.Result("${Result}");
                                // строка, соответствующая шаблону
                                //string Result = ResultMatch.Value;
                            }

                            else
                            {
                                //Console.WriteLine("FAIL");
                            }

                            // Если номер > 10 знаков, значит это LID и обрезаем до 10 знаков
                            if (RID.Length > 10)
                            {
                                //Console.WriteLine(RID);
                                RID = RID.Substring(0, 10);
                                //Console.WriteLine("RES:" + RID);

                            }
                        }

                        // Запись результатов в CGM
                        InsertResultToCGM(RID, Result);

                        #region Отправка сообщения в бот

                        if (Result == "POSITIVE")
                        {
                            #region Получем из базы данные по заявке для оповещения

                            string Request = "";
                            string FIO = "";
                            string ClientCode = "";
                            string PatientId = "";

                            // подключаемся к базе, чтобы получить данные по заявке, которые отправим в оповещении
                            string CGMConnectionString = ConfigurationManager.ConnectionStrings["CGMConnection"].ConnectionString;
                            CGMConnectionString = String.Concat(CGMConnectionString, $"User Id = {user}; Password = {password}");

                            using (SqlConnection CGMconnection = new SqlConnection(CGMConnectionString))
                            {
                                CGMconnection.Open();

                                SqlCommand GetNotificationData = new SqlCommand("SELECT r.rem_rid, r.pop_pid, r.adr_kod_regvid, p.pop_enamn + ' ' + p.pop_fnamn AS FIO " +
                                                                                "FROM LABETT..remiss r INNER JOIN labett..pop p ON r.pop_pid = p.pop_pid " +
                                                                                "WHERE r.rem_rid = @rid", CGMconnection);
                                GetNotificationData.Parameters.Add(new SqlParameter("@rid", RID));
                                SqlDataReader GetNotificationDataReader = GetNotificationData.ExecuteReader();

                                if (GetNotificationDataReader.HasRows)
                                {
                                    while (GetNotificationDataReader.Read())
                                    {
                                        Request = GetNotificationDataReader.GetString(0);
                                        PatientId = GetNotificationDataReader.GetString(1);
                                        ClientCode = GetNotificationDataReader.GetString(2);
                                        FIO = GetNotificationDataReader.GetString(3);
                                    }
                                }
                                GetNotificationDataReader.Close();
                                CGMconnection.Close();
                            }
                            #endregion
                            // отправка оповещения, если результат Positive
                            var appSettings = ConfigurationManager.AppSettings;

                            foreach (var key in appSettings.AllKeys)
                            {
                                SendNotification(botClient, appSettings[key], Request, PatientId, ClientCode, FIO);
                            }
                        }
                      
                        #endregion

                        string FileName = file.Substring(AnalyzerResultPath.Length + 1);

                        // Перемещение файлов в архив или ошибки
                        if (!FileToErrorPath)
                        {
                            if (System.IO.File.Exists(ArchivePath + @"\" + FileName))
                            {
                                System.IO.File.Delete(ArchivePath + @"\" + FileName);
                            }
                            System.IO.File.Move(file, ArchivePath + @"\" + FileName);
                        }
                        else
                        {
                            if (System.IO.File.Exists(ErrorPath + @"\" + FileName))
                            {
                                System.IO.File.Delete(ErrorPath + @"\" + FileName);
                            }
                            System.IO.File.Move(file, ErrorPath + @"\" + FileName);
                            FileResultLog("File has been moved to Error folder.");
                            FileResultLog($"");
                        }
                    }
                }
                catch (Exception Error)
                {
                   // Console.WriteLine(Error);
                }
                Thread.Sleep(1000);
            }
        }

        // Чтение данных из порта
        static void ReadFromCOM()
        {
            //while (ServiceIsActive)
            while (_continue)
            {
                // считываем все доступные байты в строку
                string messageFromBactec = _serialPort.ReadExisting();
                
                Thread.Sleep(100);

                if (messageFromBactec.Length == 0)
                {
                   // Console.WriteLine("Empty message");
                   // ExchangeLog("Empty message");
                }
                else
                {
                    //Console.WriteLine($"{messageFromBactec}");
                    ExchangeLog("");
                    // UTF8 encoder
                    // UTF8Encoding utf8 = new UTF8Encoding();
                    Encoding utf8 = Encoding.UTF8;
                    // Convert the string into a byte array.
                    byte[] encodedMessage = utf8.GetBytes(messageFromBactec);

                    //Пишем в лог запрос от прибора
                    string request = TranslateBytes(encodedMessage[0]);
                    //ExchangeLog("");
                    ExchangeLog($"C: {request};");

                    bool EndOfMessage = false; // флаг конца сообщения
                    string AllMessage = "";    // полное сообщение для записи в результирующий файл

                    // Если прибор инициирует связь (отправляет ENQ)
                    if (encodedMessage[0] == ENQ[0])
                    {
                        // На запрос ENQ, драйвер должен ответить ACK
                        _serialPort.Write(ACK, 0, ACK.Length);
                        ExchangeLog($"S:<ACK>;");

                        while (!EndOfMessage)
                        {
                            string TMPString = "";
                            try
                            {
                                string TMPMessage = _serialPort.ReadLine();

                                // ExchangeLog($"Client:{TMPMessage}"); // в логе будут кривые символы, если открывать через блокнот

                                // Преобразовываем строку в массив байтов
                                byte[] TMPencodedMessage = utf8.GetBytes(TMPMessage);

                                // Для удобства дальнейшего чтения логов, формируем строку из считанного массива байт, заменяя управляющие байты, на символы UTF8
                                // иначе будут нечитаемые символы
                                TMPString = GetStringFromBytes(TMPencodedMessage);
                                // пишем сообщение от прибора в лог обмена
                                ExchangeLog($"C:{TMPString};");
                                // отрезаем символ конца строки и контрольную сумму
                                if (TMPString.IndexOf("<ETX>") != -1)
                                {
                                    TMPString = TMPString.Substring(0, TMPString.IndexOf("<ETX>"));
                                }
                                // убираем оставшиеся управляющие символы
                                TMPString = TMPString.Replace("<CR>", "").Replace("<STX>", "");

                                // подтверждаем получение
                                _serialPort.Write(ACK, 0, ACK.Length);
                                ExchangeLog("S:<ACK>;");
                            }
                            catch (Exception ex)
                            {
                                ExchangeLog($"{ex}");
                                _serialPort.Write(ACK, 0, ACK.Length);
                                ExchangeLog("S:<ACK>;");
                                break;
                            }

                            // AllMessage = AllMessage + TMPString + '\r';
                            MakeAnalyzerResultFile(TMPString);
                        }
                    }
                }
                Thread.Sleep(WaitTimeOut);
            }
        }

        static void COMPortSettings()
        {
            Thread readThread = new Thread(ReadFromCOM);
            readThread.Name = "ReadCOM";
            ListOfThreads.Add(readThread);
            try
            {
                // Create a new SerialPort object
                _serialPort = new SerialPort();

                // настройки СОМ порта
                _serialPort.PortName = COMPortName;
                _serialPort.BaudRate = 9600;
                _serialPort.Parity = (Parity)Enum.Parse(typeof(Parity), "None", true);
                _serialPort.DataBits = 8;
                _serialPort.StopBits = (StopBits)Enum.Parse(typeof(StopBits), "One", true);
                _serialPort.Handshake = (Handshake)Enum.Parse(typeof(Handshake), "None", true);

                // Set the read/write timeouts
                _serialPort.ReadTimeout = 500;
                _serialPort.WriteTimeout = 500;

                _serialPort.Open();
                _continue = true;

                // Запуск потока чтения порта
                readThread.Start();
                Console.WriteLine("Reading thread is started");
                ServiceLog("Reading thread is started");
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR. Port cannot be opened:" + ex.ToString());
                ServiceLog("ERROR. Port cannot be opened:" + ex.ToString());
                return;
            }

        }
        static void Main(string[] args)
        {
            ServiceIsActive = true;
            Console.WriteLine("Service starts working");
            ServiceLog("Service starts working");

            #region запуск телеграм-бота
            // запуск телеграм-бота
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            // токен бота
            //var botClient = new TelegramBotClient("5713460548:AAHAem3It_bVQQrMcRvX2QNy7n5m_IUqLMY");

            Console.WriteLine("Запущен бот " + botClient.GetMeAsync().Result.FirstName + " ID: " + botClient.GetMeAsync().Id);

            // Код ниже - для общения с ботом, получения информации о сообщениях и пользователях.
            // Под нашу задачу на данный момент это не требуется, нужно просто отправлять оповещение в групповой чат
            /*
            var cts = new CancellationTokenSource();
            var cancellationToken = cts.Token;

            // StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>() // receive all update types

            };

            botClient.StartReceiving(HandleUpdateAsync,
                                     HandlePollingErrorAsync,
                                     receiverOptions,
                                     cancellationToken);
            */

            #endregion

            //Поток, который следит за другими потоками
            Thread ManagerThread = new Thread(CheckThreads);
            ManagerThread.Name = "Thread Manager";
            // ManagerThread.Start(); временно

            // Настраиваем и запускаем поток чтения из ком порта
            //COMPortSettings(); временно

            // Поток обработки результатов
            Thread ResultProcessingThread = new Thread(ResultsProcessing);
            ResultProcessingThread.Name = "ResultsProcessing";
            ListOfThreads.Add(ResultProcessingThread);
            ResultProcessingThread.Start();

            Console.WriteLine("Service is working");
            ServiceLog("Service is working");


            Console.ReadLine();

            // Send cancellation request to stop bot
            //cts.Cancel();
        }
    }
}
