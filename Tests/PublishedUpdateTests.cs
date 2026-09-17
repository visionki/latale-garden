using System;
using System.IO;
using System.Linq;
using System.Threading;
using LaTaleGarden;

public static class PublishedUpdateTests
{
    public static int Main(string[] args)
    {
        try
        {
            bool local = args.Length > 1 && args[1] == "--local";
            var client = new UpdateClient();
            byte[] feedBytes = local ? File.ReadAllBytes(Path.Combine(args[0], "updates.json")) : client.Document(UpdateProtocol.FeedUrl, CancellationToken.None);
            byte[] announcementBytes = local ? File.ReadAllBytes(Path.Combine(args[0], "announcements.json")) : client.Document(UpdateProtocol.AnnouncementsUrl, CancellationToken.None);
            var feed = UpdateProtocol.ReadFeed(feedBytes, UpdateProtocol.PublicKey);
            var release = feed.Releases.Single(x => x.Version == Program.Version);
            var announcements = UpdateProtocol.ReadAnnouncements(announcementBytes, UpdateProtocol.PublicKey);
            if (UpdateProtocol.Select(feed, "1.1.2", true, 30000, 600000) == null || UpdateProtocol.Select(feed, Program.Version, true, 30000, 600000) != null)
                throw new Exception("Version selection failed.");
            Console.WriteLine("PASS release feed signature and version selection: " + release.Version);
            Console.WriteLine("PASS signed announcement feed: " + announcements.Items.Length + " item(s)");
            if (!local)
            {
                Directory.CreateDirectory(args[0]); string path = Path.Combine(args[0], "LaTaleGarden.exe");
                client.Download(release, path, null, CancellationToken.None);
                if (UpdateProtocol.Hash(path) != UpdateProtocol.Hash(AppPaths.Executable)) throw new Exception("Published binary differs from the tested build.");
                Console.WriteLine("PASS public Release download matches the tested executable: " + release.Sha256);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
