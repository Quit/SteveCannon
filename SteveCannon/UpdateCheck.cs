using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SteveCannon
{
    /// <summary>
    /// What we did with this fail, if anything
    /// </summary>
    enum UpdateAction
    {
        /// <summary>No action has yet been decided</summary>
        Unknown,
        /// <summary>No action required</summary>
        None,
        /// <summary>Update has been performed, or is necessary</summary>
        Update,
        /// <summary>Failure</summary>
        Fail
    }

    class UpdateException : Exception
    {
        public readonly Update Update;

        public UpdateException(Update update) : base() { this.Update = update; }
        public UpdateException(Update update, string message) : base(message) { this.Update = update; }
        public UpdateException(Update update, string message, Exception innerException) : base(message, innerException) { this.Update = update; }
    }

    class Update
    {
        /// <summary>List of JSON uris that we have cached</summary>
        protected static Dictionary<Uri, JToken> CachedResponses = new Dictionary<Uri, JToken>();

        /// <summary>The .smod file we're inspecting</summary>
        public readonly FileInfo Archive;

        /// <summary>Name of the mod (i.e. minus .smod)</summary>
        public readonly string ModName;

        /// <summary>The Uri we're operating on</summary>
        protected Uri Uri;

        /// <summary>The last time our archive was written to</summary>
        public DateTime ArchiveModifiedTime { get { return Archive.LastWriteTime; } }

        /// <summary>The time the file was updated on the server</summary>
        public DateTime? ServerModifiedTime { get; protected set; }

        /// <summary>The last modified time that was reported by anyone</summary>
        protected DateTime? LatestLastModifiedTime;

        /// <summary>The action we will take</summary>
        public UpdateAction Action { get; protected set; }

        /// <summary>In case an exception happens anywhere, we'll catch it here.</summary>
        public Exception Exception { get; protected set; }

        /// <summary>Whether we were redirected or not</summary>
        public bool Recursive { get; protected set; }

        /// <summary>Creates a new instance but does nothing</summary>
        /// <param name="archive"></param>
        /// <param name="updateUri"></param>
        public Update(FileInfo archive, Uri uri)
        {
            if (archive == null)
                throw new ArgumentNullException("archive");

            if (uri == null)
                throw new ArgumentNullException("uri");

            this.Archive = archive;
            this.ModName = Path.GetFileNameWithoutExtension(archive.Name);
            this.Uri = uri;
            this.Action = UpdateAction.Unknown;
        }

        /// <summary>
        /// Creates a new request that we can use for stuff.
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        protected HttpWebRequest createRequest()
        {
            HttpWebRequest request = HttpWebRequest.CreateHttp(this.Uri);
            request.UserAgent = "SteveCannon " + Assembly.GetExecutingAssembly().GetName().Version;
            request.Timeout = 10000; // 10s should be fine; TODO: config!
            request.Proxy = null; // with a proxy, this suddenly takes *forever*
            return request;
        }

        /// <summary>
        /// Checks the uri given in <paramref name="updateInformation"/> for an update and updates the reference accordingly
        /// </summary>
        /// <param name="updateInformation"></param>
        public void Check()
        {
            //if (this.Action != UpdateAction.Unknown)
            //    throw new InvalidOperationException("Action for this Update was already determined");

            // Did we cache this one already?
            if (CachedResponses.ContainsKey(this.Uri))
            {
                Console.WriteLine("Got already an answer in our cache for {0}", this.Uri);
                checkJson();
                return;
            }

            try
            {
                Console.WriteLine("Create request object");
                HttpWebRequest request = createRequest();
                Console.WriteLine("Request object created");
                request.Method = "HEAD";
                request.IfModifiedSince = this.ArchiveModifiedTime;
                //request.BeginGetResponse(this.onResponseReceived, null);

                // Fire 'em up
                Console.WriteLine("Shots fired");

                string contentType = null;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    Console.WriteLine("Response received.");
                    // We got a response. Is it a good response?
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (response.Headers.AllKeys.Contains("Last-Modified"))
                        {
                            Console.WriteLine("Contains a Last-Modified date...");
                            // Is the last-modified time older?
                            ServerModifiedTime = response.LastModified;
                            if (ServerModifiedTime <= ArchiveModifiedTime)
                            {
                                Console.WriteLine("Last-Modified {0} is older than {1}; no action required", ServerModifiedTime, ArchiveModifiedTime);
                                // No update required!
                                Action = UpdateAction.None;
                                return;
                            }
                            else
                                Action = UpdateAction.Update;
                        }
                        contentType = response.ContentType;
                    }
                    else
                        throw new UpdateException(this, "Unexpected server response: " + response.StatusCode);
                }

                // Clear up before we proceed, supposedly HttpWeb* can be a bit... icky
                request = null;
                GC.Collect();

                // Possible answers: 
                // "text/plain; charset=..." (DROPBOX JSON) <- pretty sure JSON
                // "application/octet-stream" (DROPBOX SMOD) <- kinda sure?
                // "application/json" (APACHE JSON) <- pretty sure JSON
                // (empty string) (APACHE SMOD) <- absolutely not sure, although we could guess
                // and boy I bet there's more
                // But all of them require the Stream, sooooo
                using (Stream stream = this.downloadUri())
                {
                    if (contentType == "application/json" || contentType.IndexOf("text/plain") == 0)
                        downloadJson(stream);
                    //else if (type == "application/octet-stream")
                    //    downloadFile(stream);
                    else // We can't really say anything for sure otherwise. :|
                        checkStream(stream);
                }
            }
            catch (WebException ex)
            {
                // Because this glorious framework throws vexing exceptions...
                HttpWebResponse response = ex.Response as HttpWebResponse;

                // We still got a response?
                if (response != null)
                {
                    // Check if it's 304
                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        Console.WriteLine("Server replied 304 Not Modified");
                        this.Action = UpdateAction.None;
                        return;
                    }

                    Console.Error.WriteLine("WebException {0} ({3}): Server replied with {1} {2}", ex.Message, response.StatusCode, response.StatusDescription, ex.Status);
                }
                else
                    Console.Error.WriteLine("WebException {1}: {0}", ex.Message, ex.Status);

                // Either way, it's a failure
                this.Action = UpdateAction.Fail;
                this.Exception = ex;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("{0}: {1}; TB: {2}", ex.GetType().ToString(), ex.Message, ex.StackTrace);
                this.Action = UpdateAction.Fail;
                this.Exception = ex;
            }
        }

        /// <summary>
        /// Whether identified or not, we wish to download this uri as a stream.
        /// We kind-of expect the callee to clean said stream up.
        /// </summary>
        protected Stream downloadUri()
        {
            Console.WriteLine("Fetch {0}...", this.Uri);
            // TODO: Add exceptions here? But we would already be protected...
            HttpWebRequest request = createRequest();
            Stream outStream = new MemoryStream();
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                using (Stream inStream = response.GetResponseStream())
                    inStream.CopyTo(outStream);

                LatestLastModifiedTime = response.LastModified;
            }

            outStream.Seek(0, SeekOrigin.Begin);
            return outStream;
        }

        /// <summary>We have identified something as json and wish to do stuff.</summary>
        /// <param name="updateInformation"></param>
        /// <param name="stream"></param>
        protected void downloadJson(Stream stream)
        {
            Console.WriteLine("Parse JSON");
            try
            {
                JToken json;
                using (StreamReader streamReader = new StreamReader(stream))
                using (JsonReader jsonReader = new JsonTextReader(streamReader))
                    json = JObject.ReadFrom(jsonReader);

                // Cache it for future uses
                CachedResponses[this.Uri] = json;

                // Does it contain our value?
                checkJson(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine("During JSON parsing, {0} happened: {1}", ex.GetType().ToString(), ex.Message);
                this.Action = UpdateAction.Fail;
                throw new UpdateException(this, "Could not parse JSON", ex);
            }
        }

        protected void checkStream(Stream stream)
        {
            Console.WriteLine("Check Stream");
            // oook
            using (BinaryReader reader = new BinaryReader(stream))
            {
                // Try to identify the first few bytes.
                byte[] geoffers = reader.ReadBytes(4);
                int first = BitConverter.ToInt32(geoffers, 0);
                if (first == 0x04034b50)
                {
                    Console.WriteLine("Likely is... a zip?");
                    MemoryStream other = new MemoryStream();
                    other.Write(geoffers, 0, 4);
                    stream.CopyTo(other);
                    other.Seek(0, SeekOrigin.Begin);
                    stream.Dispose();

                    // Check for integrity
                    try
                    {
                        var entries = new System.IO.Compression.ZipArchive(other).Entries;
                        other.Seek(0, SeekOrigin.Begin);
                    }
                    catch (Exception ex)
                    {
                        throw new UpdateException(this, "Server likely replied with a zip; but ZipArchive failed: " + ex.Message, ex);
                    }

                    downloadFile(other);
                }
                else if (geoffers[0] == '{')
                {
                    Console.WriteLine("Likely is... a json?");
                    MemoryStream other = new MemoryStream();
                    other.Write(geoffers, 0, 4);
                    stream.CopyTo(other);
                    other.Seek(0, SeekOrigin.Begin);
                    stream.Dispose();
                    downloadJson(other);
                }
                else
                    throw new UpdateException(this, "No idea what " + (first) + " could indicate as file type.");
            }
        }

        /// <summary>Checks the (cached) json for an entry</summary>
        protected void checkJson()
        {
            checkJson(CachedResponses[this.Uri]);
        }

        protected void checkJson(JToken json)
        {
            if (this.Recursive)
                throw new UpdateException(this, "JSON redirected twice");

            this.Recursive = true;

            // Contains our vaalue?
            JToken value = json[this.ModName];
            if (value == null)
                throw new UpdateException(this, "Mod not found in JSON");

            // Simple: It's a URL redirection
            if (value.Type == JTokenType.String)
            {
                // Set new location + go again!
                this.Uri = new Uri(value.Value<string>());
                Console.WriteLine("JSON redirects us to {0}", this.Uri);
                this.Check();
            }
            else if (value.Type == JTokenType.Object)
            {
                // OK, we need to have an URI entry
                JToken uri = value["uri"];

                if (uri == null)
                    throw new UpdateException(this, "Mod exists in JSON, but uri is not defined");

                if (uri.Type != JTokenType.String)
                    throw new UpdateException(this, "Mod specified URI, but is not a string.");

                // No last-modified has been set; set the uri
                this.Uri = new Uri(uri.Value<string>());
                Console.WriteLine("JSON redirects us to {0}", this.Uri);

                // Does it specify a last_modified field?
                JToken lastModified = value["last_modified"];
                if (lastModified != null)
                {
                    // OK..?
                    if (lastModified.Type == JTokenType.Date)
                    {
                        this.ServerModifiedTime = lastModified.Value<DateTime>();
                    }
                    else if (lastModified.Type == JTokenType.String)
                    {
                        // ... try to parse it
                        DateTime result;
                        if (!DateTime.TryParse(lastModified.Value<string>(), out result))
                            throw new UpdateException(this, "Unable to parse date string provided (" + lastModified.Value<string>() + ")");
                        this.ServerModifiedTime = result;
                    }
                    else if (lastModified.Type == JTokenType.Integer)
                    {
                        this.ServerModifiedTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(lastModified.Value<int>());
                    }
                    else
                        throw new UpdateException(this, "Expected last_modified to be either a string or a value but got " + lastModified.Type.ToString());

                    // Now that we got the time, check it.
                    if (this.ServerModifiedTime > this.ArchiveModifiedTime)
                    {
                        Console.WriteLine("JSON reports last_modified {0}, which is newer than {1}", this.ServerModifiedTime, this.ArchiveModifiedTime);
                        this.Action = UpdateAction.Update;
                        this.downloadFile();
                        return;
                    }

                    Console.WriteLine("JSON reports last_modified {0}, which is older than {1}", this.ServerModifiedTime, this.ArchiveModifiedTime);
                    this.Action = UpdateAction.None;

                    return;
                }

                // vOv check the new URI I guess. It /should/ be a smod archive.
                this.Check();
            }
            else
                throw new UpdateException(this, "JSON contained " + value.Type + "; expected object/string");
        }

        protected void downloadFile()
        {
            using (Stream str = downloadUri())
                downloadFile(str);
        }

        protected void downloadFile(Stream stream)
        {
            Console.WriteLine("Attempt to write {0}", this.Archive.Name);
            using (FileStream fStr = this.Archive.Open(FileMode.Truncate))
                stream.CopyTo(fStr);

            // Set the proper time
            if (this.ServerModifiedTime.HasValue)
                File.SetLastWriteTime(this.Archive.FullName, this.ServerModifiedTime.Value);
            else
                Console.WriteLine("[WARNING] Unable to find LastModified date; the current local time will be used.");

            Console.WriteLine("Done.");
        }
    }
}
