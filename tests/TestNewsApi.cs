using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

class TestNewsApi
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== NewsAPI Connectivity Test ===");

        // 1. 讀取 appsettings.json 取得 API Key
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        string apiKey = config["NewsApi:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("Error: API Key not found in appsettings.json");
            return;
        }

        Console.WriteLine($"Using API Key: {apiKey.Substring(0, 5)}...");

        // 2. 測試關鍵字
        string query = "區塊鏈";
        string url = $"https://newsapi.org/v2/top-headlines?q={Uri.EscapeDataString(query)}&country=tw&pageSize=3&apiKey={apiKey}";

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "NewsApiTest/1.0");

        try
        {
            Console.WriteLine($"Requesting: {url}");
            var response = await client.GetAsync(url);
            
            Console.WriteLine($"Status: {response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var status = doc.RootElement.GetProperty("status").GetString();
                int totalResults = doc.RootElement.GetProperty("totalResults").GetInt32();

                Console.WriteLine($"API Status: {status}");
                Console.WriteLine($"Total Results: {totalResults}");

                if (totalResults > 0)
                {
                    var articles = doc.RootElement.GetProperty("articles");
                    Console.WriteLine("\nTop Headlines Found:");
                    foreach (var article in articles.EnumerateArray())
                    {
                        Console.WriteLine($"- {article.GetProperty("title").GetString()}");
                    }
                }
                else
                {
                    Console.WriteLine("\nNo results found for '區塊鏈' in Taiwan headlines.");
                    Console.WriteLine("Hint: Try a broader query like '台灣' if this fails.");
                }
            }
            else
            {
                Console.WriteLine($"Error Body: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex.Message}");
        }
    }
}
