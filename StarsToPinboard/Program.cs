using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

using Newtonsoft.Json.Linq;

namespace StarsToPinboard
{
    class Program
    {
        static void Main(string[] args)
        {
            var githubUser = ConfigurationManager.AppSettings["GithubUser"];
            var pinboardKey = ConfigurationManager.AppSettings["PinboardApiKey"];
            var log = new TraceWriter();

            var process = new Process(log, githubUser, pinboardKey);
            process.Execute().GetAwaiter().GetResult();


            Console.ReadKey();
        }
    }

    public class TraceWriter
    {
        public void Info(string message)
        {
            Console.WriteLine(message);
        }
    }


    class Process
    {
        private readonly string _githubUsername;
        private readonly string _pinboardApiKey;

        private TraceWriter Log { get; }

        public Process(TraceWriter log, string githubUsername, string pinboardApiKey)
        {
            _githubUsername = githubUsername;
            _pinboardApiKey = pinboardApiKey;
            Log = log;
        }

        public async Task Execute()
        {
            var since = DateTime.UtcNow.AddHours(-4);
            Log.Info($"Checking for stars since {since:O}");

            var stars = await GetStars(since);

            await Bookmark(stars);
        }

        private async Task Bookmark(IEnumerable<Star> stars)
        {
            var client = new HttpClient();

            int i = 0;
            foreach (var star in stars)
            {
                var query = new StringBuilder()
                    .Append("auth_token=").Append(Uri.EscapeDataString(_pinboardApiKey))
                    .Append("&url=").Append(Uri.EscapeDataString(star.Url))
                    .Append("&description=").Append(Uri.EscapeDataString(star.Description ?? "A github project"))
                    .Append("&tags=").Append(Uri.EscapeDataString("github starred " + star.Language))
                    .ToString();
                var uri = "https://api.pinboard.in/v1/posts/add?" + query;

                Log.Info($"Bookmarking {star.Name} to pinboard.");
                var response = await client.GetAsync(uri);
                response.EnsureSuccessStatusCode();

                Thread.Sleep(200);
                i++;
            }

            Log.Info($"Bookmarked {i} repos.");
        }

        private async Task<IEnumerable<Star>> GetStars(DateTime since)
        {
            var endpoint = $"https://api.github.com/users/{_githubUsername}/starred";
            var client = new HttpClient();

            // custom Accepts header that gets us dates
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3.star+json"));

            // required by API TOS
            client.DefaultRequestHeaders.Add("User-Agent", _githubUsername);


            var stars = new List<Star>();

            while (endpoint != null)
            {
                Log.Info("Loading " + endpoint);
                var response = await client.GetAsync(endpoint);
                var links = response.Headers.GetValues("Link");
                var json = await response.Content.ReadAsStringAsync();

                var page = ParseStars(json).ToList();
                stars.AddRange(page.Where(s => s.StarredAt >= since));

                if (page.All(s => s.StarredAt >= since))
                {
                    Log.Info("Page is all new stars");
                    endpoint = ParseNextLink(links.First());
                }
                else
                {
                    Log.Info("No need to search for new stars.");
                    break;
                }
            }

            return stars;
        }

        private static string ParseNextLink(string linksHeader)
        {
            // <https://api.github.com/user/3689068/starred?page=2>; rel=\"next\", <https://api.github.com/user/3689068/starred?page=3>; rel=\"last\"
            var nextEntry = linksHeader
                .Split(',')
                .FirstOrDefault(link => link.Contains("rel=\"next\""));

            if (nextEntry == null)
                return null;

            var match = Regex.Match(nextEntry, "<(.*)>;");
            if (match.Success && match.Groups.Count == 2)
            {
                return match.Groups[1].Value;
            }

            return null;
        }

        private static IEnumerable<Star> ParseStars(string json)
        {
            var root = JArray.Parse(json);
            return root.Children().Select(elem => new Star()
            {
                Name = elem["repo"]["name"].Value<string>(),
                Url = elem["repo"]["html_url"].Value<string>(),
                Description = elem["repo"]["description"].Value<string>(),
                Language = elem["repo"]["language"].Value<string>(),
                StarredAt = elem["starred_at"].Value<DateTime>()
            });
        }

        class Star
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string Url { get; set; }
            public string Language { get; set; }
            public DateTime StarredAt { get; set; }

            public override string ToString() => Name;
        }

    }
}
