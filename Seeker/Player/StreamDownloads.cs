/*
 * Copyright (c) 2026 Arsen
 *
 * This file is part of MusicBay
 *
 * MusicBay is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * MusicBay is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with MusicBay. If not, see <http://www.gnu.org/licenses/>.
 */

using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Provider;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Google.Android.Material.Dialog;
using Seeker.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Seeker.Player
{
    /// <summary>
    /// Saves streamed songs as tagged audio files in Music/MusicBay/&lt;Artist&gt; - &lt;Album&gt;, one folder per album,
    /// so the library shows them as albums. One song at a time, with progress in a notification.
    /// </summary>
    internal static class StreamDownloads
    {
        const string ChannelId = "musicbay_stream_downloads";
        const int NotificationId = 113;
        const string PrefsName = "musicbay_stream";
        const string WarnedKey = "download_warned";
        const string Root = "MusicBay";

        static readonly object gate = new object();
        static readonly Queue<Song> queue = new Queue<Song>();
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        static bool running;
        static int done, total;
        static string current, message;

        /// <summary>Raised on the main thread whenever the progress below changes.</summary>
        public static event Action Changed;
        public static int Done => done;
        public static int Total => total;
        /// <summary>The song being downloaded, or null when idle.</summary>
        public static string Current => current;
        /// <summary>The result of the last batch, shown for a few seconds after it ends; null otherwise.</summary>
        public static string Message => message;

        static readonly Android.OS.Handler main = new Android.OS.Handler(Android.OS.Looper.MainLooper);

        static void Raise() => main.Post(() => Changed?.Invoke());

        /// <summary>Queues songs for download, after a one-time note on what may be downloaded.</summary>
        public static void Start(Activity activity, IReadOnlyCollection<Song> songs)
        {
            var streamed = songs.Where(s => s.StreamId != null).ToList();
            if (streamed.Count == 0) { return; }
            // before Android 10 the Music folder is written directly, which needs the storage permission
            if (!OperatingSystem.IsAndroidVersionAtLeast(29) && ContextCompat.CheckSelfPermission(activity, Manifest.Permission.WriteExternalStorage) != Permission.Granted)
            {
                ActivityCompat.RequestPermissions(activity, new[] { Manifest.Permission.WriteExternalStorage }, 0);
                Toast.MakeText(activity, Resource.String.stream_download_permission, ToastLength.Long).Show();
                return;
            }
            var prefs = activity.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs.GetBoolean(WarnedKey, false)) { Enqueue(activity, streamed); return; }
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.stream_download_title)
                .SetMessage(Resource.String.stream_download_message)
                .SetPositiveButton(Resource.String.stream_download, (s, e) =>
                {
                    prefs.Edit().PutBoolean(WarnedKey, true).Apply();
                    Enqueue(activity, streamed);
                })
                .SetNegativeButton(Android.Resource.String.Cancel, (s, e) => { })
                .Show();
        }

        static void Enqueue(Context context, List<Song> songs)
        {
            var app = context.ApplicationContext;
            lock (gate)
            {
                foreach (var song in songs) { queue.Enqueue(song); }
                total += songs.Count;
                message = null;
                if (running) { Raise(); return; }
                running = true;
            }
            Raise();
            Toast.MakeText(context, context.Resources.GetQuantityString(Resource.Plurals.stream_downloading, songs.Count, songs.Count), ToastLength.Short).Show();
            CommonHelpers.CreateNotificationChannel(app, ChannelId, app.GetString(Resource.String.stream_download_channel));
            // shortcut: runs in the app process without a foreground service, so Android may stop it if the app is closed; move it into a service if that happens in practice
            Task.Run(() => Run(app));
        }

        static async Task Run(Context context)
        {
            int failed = 0;
            while (true)
            {
                Song song;
                lock (gate)
                {
                    if (queue.Count == 0)
                    {
                        running = false;
                        message = Finish(context, done, failed);
                        done = total = 0;
                        current = null;
                        Raise();
                        main.PostDelayed(() => { if (!running) { message = null; Changed?.Invoke(); } }, 4000);
                        return;
                    }
                    song = queue.Dequeue();
                    current = song.Title;
                }
                Raise();
                Notify(context, context.GetString(Resource.String.stream_download_progress, done + 1, total, song.Title), done, total);
                try { await Save(context, song); }
                catch (Exception ex)
                {
                    failed++;
                    Logger.Debug("stream download failed for " + song.Title + ": " + ex.Message);
                }
                lock (gate) { done++; }
                Raise();
            }
        }

        static async Task Save(Context context, Song song)
        {
            string folder = Clean(string.Join(" - ", new[] { song.Artist, song.Album }.Where(p => !string.IsNullOrWhiteSpace(p))));
            if (folder.Length == 0) { folder = "YouTube Music"; }
            var manifest = await PlayerService.Youtube.Videos.Streams.GetManifestAsync(song.StreamId);
            var audio = PlayerService.BestAudio(manifest);
            bool mp4 = audio.Container == YoutubeExplode.Videos.Streams.Container.Mp4;
            string name = Clean(song.Title) + (mp4 ? ".m4a" : ".webm");
            if (Exists(context, folder, name)) { return; }

            string temp = Path.Combine(context.CacheDir.AbsolutePath, Guid.NewGuid().ToString("N") + (mp4 ? ".m4a" : ".webm"));
            try
            {
                await PlayerService.Youtube.Videos.Streams.DownloadAsync(audio, temp);
                if (mp4) { await Tag(temp, song); } // the library reads title, artist, album and cover from these tags
                Publish(context, temp, folder, name, mp4 ? "audio/mp4" : "audio/webm");
            }
            finally
            {
                File.Delete(temp);
            }
        }

        static async Task Tag(string path, Song song)
        {
            byte[] cover = null;
            if (song.ArtUrl != null)
            {
                try { cover = await http.GetByteArrayAsync(song.ArtUrl); }
                catch (Exception) { } // a song without its cover is still worth keeping
            }
            using var file = TagLib.File.Create(path);
            file.Tag.Title = song.Title;
            file.Tag.Performers = new[] { song.Artist };
            file.Tag.AlbumArtists = new[] { song.Artist };
            file.Tag.Album = song.Album;
            string mime = cover == null || cover.Length < 4 ? null
                : cover[0] == 0xFF && cover[1] == 0xD8 ? "image/jpeg"
                : cover[0] == 0x89 && cover[1] == 0x50 ? "image/png"
                : null; // WebP and others are not valid MP4 cover art
            if (mime != null)
            {
                file.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = mime } };
            }
            file.Save();
        }

        static string RelativeDir(string folder) => Android.OS.Environment.DirectoryMusic + "/" + Root + "/" + folder + "/";

        static bool Exists(Context context, string folder, string name)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            {
#pragma warning disable CS0618 // the public folder path is the only option before scoped storage
                return File.Exists(Path.Combine(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryMusic).AbsolutePath, Root, folder, name));
#pragma warning restore CS0618
            }
            using var cursor = context.ContentResolver.Query(MediaStore.Audio.Media.ExternalContentUri, new[] { "_id" },
                MediaStore.IMediaColumns.RelativePath + "=? AND " + MediaStore.IMediaColumns.DisplayName + "=?", new[] { RelativeDir(folder), name }, null);
            return cursor != null && cursor.Count > 0;
        }

        static void Publish(Context context, string temp, string folder, string name, string mime)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            {
#pragma warning disable CS0618 // the public folder path is the only option before scoped storage
                string dir = Path.Combine(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryMusic).AbsolutePath, Root, folder);
#pragma warning restore CS0618
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, name);
                File.Copy(temp, target, true);
                MediaScannerConnection.ScanFile(context, new[] { target }, new[] { mime }, null);
                return;
            }
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, name);
            values.Put(MediaStore.IMediaColumns.MimeType, mime);
            values.Put(MediaStore.IMediaColumns.RelativePath, RelativeDir(folder));
            values.Put(MediaStore.IMediaColumns.IsPending, 1); // hidden from other apps until the file is complete
            var resolver = context.ContentResolver;
            var uri = resolver.Insert(MediaStore.Audio.Media.GetContentUri(MediaStore.VolumeExternalPrimary), values)
                ?? throw new IOException("MediaStore refused the file");
            try
            {
                using (var output = resolver.OpenOutputStream(uri))
                using (var input = File.OpenRead(temp))
                {
                    input.CopyTo(output);
                }
                values.Clear();
                values.Put(MediaStore.IMediaColumns.IsPending, 0);
                resolver.Update(uri, values, null, null);
            }
            catch
            {
                resolver.Delete(uri, null, null); // no half-written songs in the library
                throw;
            }
        }

        static string Clean(string text)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            string cleaned = new string((text ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return cleaned.Length > 100 ? cleaned.Substring(0, 100) : cleaned;
        }

        static void Notify(Context context, string text, int progress, int max)
        {
            Show(context, Builder(context).SetContentTitle(context.GetString(Resource.String.stream_download_title)).SetContentText(text)
                .SetProgress(max, progress, false).SetOngoing(true));
        }

        static string Finish(Context context, int count, int failed)
        {
            string text = failed == 0
                ? context.GetString(Resource.String.stream_download_done, count - failed)
                : context.GetString(Resource.String.stream_download_done_failed, count - failed, failed);
            Show(context, Builder(context).SetContentTitle(context.GetString(Resource.String.stream_download_title)).SetContentText(text).SetAutoCancel(true));
            return text;
        }

        static Notification.Builder Builder(Context context)
        {
#pragma warning disable CS0618 // the channel constructor needs API 26; minSdk is 23
            var builder = OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(context, ChannelId) : new Notification.Builder(context);
#pragma warning restore CS0618
            return builder.SetSmallIcon(Resource.Drawable.ic_stat_soulseekicontransparent).SetOnlyAlertOnce(true);
        }

        static void Show(Context context, Notification.Builder builder)
        {
            try { ((NotificationManager)context.GetSystemService(Context.NotificationService)).Notify(NotificationId, builder.Build()); }
            catch (Java.Lang.SecurityException) { } // notifications turned off: the files still arrive in the library
        }
    }
}
