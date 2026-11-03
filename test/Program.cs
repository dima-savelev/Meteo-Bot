using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Telegram.Bot;

string Normalize(string text)
{
    if (string.IsNullOrWhiteSpace(text))
        return "";

    return Regex.Replace(text, @"\s+", " ")
        .Trim()
        .ToLower();
}

// ⏳ Ждем 8 секунд при старте, чтобы docker-warp-native успел поднять интерфейс warp
Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ⏳ Ожидание запуска интерфейса WARP...");
await Task.Delay(TimeSpan.FromSeconds(8));

// 🌐 Жестко привязываем все исходящие соединения к внутреннему IP контейнера docker-warp-native (172.16.0.2)
var warpHandler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),

    ConnectCallback = async (context, cancellationToken) =>
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Привязываем сокет строго к интерфейсу WARP (172.16.0.2)
            socket.Bind(new IPEndPoint(IPAddress.Parse("172.16.0.2"), 0));

            // Разрешаем DNS и подключаемся к целевому хосту
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
};

var client = new HttpClient(warpHandler);
client.Timeout = TimeSpan.FromSeconds(30);

// Передаем тот же защищенный client в TelegramBotClient, чтобы и запросы к TG шли через WARP
var bot = new TelegramBotClient("TOKEN");

var chatIds = new List<long>
{
    //ID чатов
};

string url = "https://meteo62.ru/opnmu";

bool siteDown = false;
bool pageNotFound = false;
bool textMissing = false;
bool unknownError = false;
string lastText = null;

// 🛡️ Хелпер безопасной отправки сообщений без риска краша бота
async Task SafeSendMessage(long chatId, string text)
{
    try
    {
        await bot.SendMessage(chatId, text);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ⚠️ Не удалось отправить сообщение в TG ({chatId}): {ex.Message}");
    }
}

async Task BroadcastMessage(string text)
{
    foreach (var id in chatIds)
    {
        await SafeSendMessage(id, text);
    }
}

// 🚀 Стартовое сообщение с проверкой внешнего IP
string ipInfo;
try
{
    var currentIp = await client.GetStringAsync("https://api.ipify.org");
    ipInfo = currentIp.Trim();
    Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] 🚀 Бот запущен. Внешний IP: {ipInfo}");
}
catch (Exception ex)
{
    ipInfo = "не удалось определить";
    Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ⚠️ Не удалось получить внешний IP: {ex.Message}");
}

await BroadcastMessage($"🚀 Бот запущен\n🌐 Внешний IP: {ipInfo}");

// 🔧 функция обработки падения сайта
async Task HandleSiteDown(string reason)
{
    // 1. Быстрая перепроверка через 20 сек (на случай разового сетевого лага)
    await Task.Delay(TimeSpan.FromSeconds(20));
    try
    {
        var quickCheck = await client.GetAsync(url);
        if (quickCheck.IsSuccessStatusCode)
        {
            Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ⚠️ Разовый лаг ({reason}), при повторной проверке сайт ответил. Игнорируем.");
            return;
        }
    }
    catch
    {
        // Сайт действительно не ответил — переходим к ожиданию
    }

    // 2. Ждем 5 минут перед первой отправкой алерта
    Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] 🚨 Сайт не ответил дважды. Ждем 5 минут перед алертом...");
    await Task.Delay(TimeSpan.FromMinutes(5));

    try
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ✅ Сбой ({reason}) прошел за 5 минут, сайт снова работает.");
        return;
    }
    catch
    {
        if (!siteDown)
        {
            siteDown = true;
            Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] 🚨 Отправка алертов о падении в Telegram...");
            await BroadcastMessage($"⚠️ Сайт недоступен\nПричина: {reason}\nОжидаем восстановления...");
        }

        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(30));

            try
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                if (siteDown)
                {
                    siteDown = false;
                    Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ✅ Сайт восстановился. Отправляю уведомления в Telegram...");
                    await BroadcastMessage("✅ Сайт восстановился. Продолжаю работу.");
                }

                // 3. Штатная пауза после восстановления, чтобы разорвать спам-петлю
                await Task.Delay(TimeSpan.FromMinutes(30));
                break;
            }
            catch
            {
                Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] Сайт всё ещё недоступен...");
            }
        }
    }
}

while (true)
{
    try
    {
        var response = await client.GetAsync(url);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (!pageNotFound)
            {
                pageNotFound = true;
                Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ❌ Ошибка 404: страница не найдена. Возможно, изменился URL.");
                await BroadcastMessage("❌ Ошибка 404: страница не найдена. Возможно, изменился URL.");
            }
            await Task.Delay(TimeSpan.FromMinutes(30));
            continue;
        }
        else
        {
            if (pageNotFound)
            {
                Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ✅ Страница снова доступна");
                await BroadcastMessage("✅ Страница снова доступна");
            }
            pageNotFound = false;
        }

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var mainDiv = doc.GetElementbyId("nmu");
        var date = mainDiv?.SelectSingleNode(".//div[@class='date']");
        var text = mainDiv?.SelectSingleNode(".//div[@class='text']");
        var newText = text?.InnerText.Trim();

        if (mainDiv == null || string.IsNullOrWhiteSpace(newText))
        {
            if (!textMissing)
            {
                Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ⚠️ Текста нет");
                textMissing = true;
            }

            await Task.Delay(TimeSpan.FromMinutes(30));
            continue;
        }
        else
        {
            if (textMissing) textMissing = false;
            if (unknownError) unknownError = false;

            var dateText = date?.InnerText.Trim() ?? "Дата не указана";
            var normalizedNew = Normalize(newText);

            if (lastText == null)
            {
                lastText = newText;
                Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] Начальное значение сохранено");
                await BroadcastMessage($"⚠️ Обновление НМУ:\n📅 {dateText}\n{newText}");
            }
            else
            {
                var normalizedLast = Normalize(lastText);
                if (normalizedNew != normalizedLast)
                {
                    Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ⚠️ Обновление!");
                    await BroadcastMessage($"⚠️ Обновление НМУ:\n📅 {dateText}\n{newText}");
                    lastText = newText;
                }
            }
        }

        await Task.Delay(TimeSpan.FromMinutes(30));
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] ⏱️ Таймаут");
        await HandleSiteDown("⏱️ Таймаут (сайт долго не отвечает)");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] 🌐 Сетевая ошибка: {ex.Message}");
        await HandleSiteDown("🌐 Ошибка подключения к сайту");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] 🔥 Неизвестная ошибка: {ex.Message}");

        if (!unknownError)
        {
            unknownError = true;
            await BroadcastMessage($"❌ Неизвестная ошибка:\n{ex.Message}");
        }

        await Task.Delay(TimeSpan.FromMinutes(30));
    }
}