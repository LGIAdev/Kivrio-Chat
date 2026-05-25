using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace KivrioChat
{
    internal sealed class RequestBodyTooLargeException : Exception
    {
        public RequestBodyTooLargeException(string message) : base(message)
        {
        }
    }

    internal sealed class UploadValidationException : Exception
    {
        public readonly HttpStatusCode StatusCode;

        public UploadValidationException(HttpStatusCode statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    internal static class LocalDependencyResolver
    {
        private static readonly object LockObject = new object();
        private static string _pdfPigDirectory;
        private static bool _registered;

        public static void Register(string root)
        {
            lock (LockObject)
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    _pdfPigDirectory = Path.Combine(Path.GetFullPath(root), "server", "lib", "pdfpig");
                }

                if (_registered)
                {
                    return;
                }

                AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
                _registered = true;
            }
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string directory = _pdfPigDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            string assemblyName = new AssemblyName(args.Name).Name + ".dll";
            string candidate = Path.Combine(directory, assemblyName);
            if (!File.Exists(candidate))
            {
                return null;
            }

            return Assembly.LoadFrom(candidate);
        }
    }

    internal sealed class PdfTextExtractionResult
    {
        public int PageCount;
        public string Text;
        public bool Truncated;
    }

    internal static class PdfTextExtractor
    {
        public static PdfTextExtractionResult ExtractText(string filePath, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new InvalidOperationException("Fichier PDF introuvable.");
            }

            int limit = Math.Max(1, maxChars);
            var builder = new StringBuilder();
            bool truncated = false;
            int pageCount;

            using (PdfDocument document = PdfDocument.Open(filePath))
            {
                pageCount = document.NumberOfPages;
                bool firstPage = true;
                foreach (var page in document.GetPages())
                {
                    if (!firstPage)
                    {
                        AppendLimited(builder, "\n\n", limit, ref truncated);
                    }

                    firstPage = false;
                    string pageText = ExtractPageText(page);
                    AppendLimited(builder, pageText, limit, ref truncated);
                    if (truncated)
                    {
                        break;
                    }
                }
            }

            return new PdfTextExtractionResult
            {
                PageCount = pageCount,
                Text = builder.ToString().Trim(),
                Truncated = truncated
            };
        }

        private static string ExtractPageText(UglyToad.PdfPig.Content.Page page)
        {
            try
            {
                return ContentOrderTextExtractor.GetText(page, true) ?? "";
            }
            catch
            {
                return page == null ? "" : (page.Text ?? "");
            }
        }

        private static void AppendLimited(StringBuilder builder, string value, int maxChars, ref bool truncated)
        {
            if (builder == null || truncated || string.IsNullOrEmpty(value))
            {
                return;
            }

            int remaining = maxChars - builder.Length;
            if (remaining <= 0)
            {
                truncated = true;
                return;
            }

            if (value.Length <= remaining)
            {
                builder.Append(value);
                return;
            }

            builder.Append(value.Substring(0, remaining));
            truncated = true;
        }
    }

    internal static class ErrorResponses
    {
        public static HttpStatusCode StatusFor(Exception ex)
        {
            if (ex is RequestBodyTooLargeException)
            {
                return (HttpStatusCode)413;
            }
            if (ex is UploadValidationException)
            {
                return ((UploadValidationException)ex).StatusCode;
            }
            return HttpStatusCode.InternalServerError;
        }

        public static string MessageFor(Exception ex)
        {
            if (ex is RequestBodyTooLargeException)
            {
                return "Requete trop volumineuse.";
            }
            if (ex is UploadValidationException)
            {
                return ex.Message;
            }
            if (IsKnownPublicInvalidOperation(ex))
            {
                return ex.Message;
            }
            return "Erreur serveur interne.";
        }

        public static string ReasonFor(Exception ex)
        {
            if (ex is RequestBodyTooLargeException) return "request_body_too_large";
            if (ex is UploadValidationException) return "upload_validation";
            if (IsKnownPublicInvalidOperation(ex)) return "invalid_request";
            return "unhandled_exception";
        }

        private static bool IsKnownPublicInvalidOperation(Exception ex)
        {
            string message = ex == null ? "" : (ex.Message ?? "");
            return ex is InvalidOperationException
                && (message == "Content-Length invalide."
                    || message == "Boundary multipart introuvable."
                    || message == "Boundary multipart invalide."
                    || message.StartsWith("Le mot de passe doit contenir au moins ", StringComparison.Ordinal)
                    || message.StartsWith("Le mot de passe ne peut pas depasser ", StringComparison.Ordinal));
        }
    }

    internal static class ServerLog
    {
        private static readonly object LockObject = new object();
        private static readonly HashSet<string> AllowedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "app",
            "host",
            "port",
            "url",
            "root_name",
            "method",
            "path",
            "remote",
            "status",
            "exception",
            "reason"
        };

        public static void Info(string eventName, IDictionary<string, object> fields)
        {
            Write("info", eventName, fields);
        }

        public static void Error(string eventName, IDictionary<string, object> fields)
        {
            Write("error", eventName, fields);
        }

        internal static string FormatLine(string level, string eventName, IDictionary<string, object> fields)
        {
            var payload = new Dictionary<string, object>();
            payload["ts"] = DateTime.UtcNow.ToString("o");
            payload["level"] = SafeString(level);
            payload["event"] = SafeString(eventName);

            if (fields != null)
            {
                foreach (var pair in fields)
                {
                    if (!AllowedFields.Contains(pair.Key) || pair.Value == null)
                    {
                        continue;
                    }

                    payload[pair.Key] = SafeValue(pair.Value);
                }
            }

            return new JavaScriptSerializer().Serialize(payload);
        }

        private static void Write(string level, string eventName, IDictionary<string, object> fields)
        {
            string line = FormatLine(level, eventName, fields);
            lock (LockObject)
            {
                Console.Error.WriteLine(line);
            }
        }

        private static object SafeValue(object value)
        {
            if (value is int || value is long || value is bool)
            {
                return value;
            }
            return SafeString(Convert.ToString(value));
        }

        private static string SafeString(string value)
        {
            string text = value ?? "";
            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            const int maxLength = 160;
            if (text.Length > maxLength)
            {
                text = text.Substring(0, maxLength);
            }
            return text;
        }
    }

    internal sealed class AuthThrottleRecord
    {
        public int Failures;
        public DateTime FirstFailureUtc;
        public DateTime LockedUntilUtc;
    }

    internal static class DurableFile
    {
        public static void WriteAllTextAtomically(string path, string content, Encoding encoding)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding ?? Encoding.UTF8))
            {
                writer.Write(content ?? "");
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                string backupPath = path + ".bak";
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Replace(tempPath, path, backupPath, true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }

        public static void WriteAllTextAtomicallyWithoutBackup(string path, string content, Encoding encoding)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding ?? Encoding.UTF8))
            {
                writer.Write(content ?? "");
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }

        public static void WriteAllBytesAtomically(string path, byte[] content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = content ?? new byte[0];
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null, true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        public static void BackupCorruptFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                string backupPath;
                do
                {
                    backupPath = path + ".corrupt-" + Guid.NewGuid().ToString("N") + ".bak";
                }
                while (File.Exists(backupPath));

                File.Move(path, backupPath);
            }
            catch
            {
            }
        }

        public static void BackupBeforeMigration(string path, int fromVersion, int toVersion)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                string backupPath;
                do
                {
                    backupPath = path
                        + ".pre-migration-v"
                        + Math.Max(0, fromVersion)
                        + "-to-v"
                        + toVersion
                        + "-"
                        + Guid.NewGuid().ToString("N")
                        + ".bak";
                }
                while (File.Exists(backupPath));

                File.Copy(path, backupPath, false);
            }
            catch
            {
            }
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            string root = null;
            string host = "127.0.0.1";
            int port = 8020;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (arg == "--help" || arg == "-h")
                {
                    Console.WriteLine("usage: kivrio-chat-server.exe [--root ROOT] [--host HOST] [--port PORT]");
                    return 0;
                }
                if (arg == "--root" && i + 1 < args.Length)
                {
                    root = args[++i];
                    continue;
                }
                if (arg == "--host" && i + 1 < args.Length)
                {
                    host = args[++i];
                    continue;
                }
                if (arg == "--port" && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out port);
                    continue;
                }
            }

            if (port <= 0)
            {
                port = 8020;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                root = Path.GetFullPath(Path.Combine(exeDir, ".."));
            }

            root = Path.GetFullPath(root);
            LocalDependencyResolver.Register(root);
            var server = new LocalServer(root, host, port);
            ServerLog.Info("server_start", new Dictionary<string, object>
            {
                { "app", "kivrio-chat" },
                { "host", host },
                { "port", port },
                { "url", "http://" + host + ":" + port + "/index.html" },
                { "root_name", RootName(root) }
            });
            EventHandler processExitHandler = delegate { server.StopManagedWebSearchRuntime(); };
            ConsoleCancelEventHandler cancelHandler = delegate { server.StopManagedWebSearchRuntime(); };
            AppDomain.CurrentDomain.ProcessExit += processExitHandler;
            Console.CancelKeyPress += cancelHandler;
            try
            {
                server.Run();
            }
            finally
            {
                server.StopManagedWebSearchRuntime();
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                Console.CancelKeyPress -= cancelHandler;
            }
            return 0;
        }

        private static string RootName(string root)
        {
            string trimmed = (root ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? "root" : name;
        }
    }

    internal sealed class LocalServer
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");
        private const string SessionCookieName = "kivro_session";
        private const int PasswordMinLength = 8;
        private const int PasswordMaxLength = 128;
        private const int Pbkdf2Iterations = 310000;
        private const int MaxAuthFailures = 5;
        private const int AuthFailureWindowSeconds = 300;
        private const int AuthLockoutSeconds = 60;
        private const int WebSearchQueryMaxLength = 400;
        private const int WebSearchDefaultMaxResults = 5;
        private const int WebSearchMaxResults = 5;
        private const int WebSearchTimeoutMs = 3000;
        private const int WebSearchHealthcheckTimeoutMs = 1000;
        private const int SearxngLauncherTimeoutMs = 30000;
        private const int SearxngStopTimeoutMs = 10000;
        private const int PdfExtractedTextMaxChars = 200000;
        private const long VoiceAudioMaxBytes = 10L * 1024L * 1024L;
        private const int VoiceDefaultTimeoutSeconds = 60;
        private const int VoiceMaxTimeoutSeconds = 180;
        private const string WebSearchBaseUrlEnv = "KIVRIO_WEB_SEARCH_BASE_URL";
        private const string WebSearchEnableManagedEnv = "KIVRIO_WEB_SEARCH_ENABLE_MANAGED";
        private const string WebSearchAllowMockEnv = "KIVRIO_WEB_SEARCH_ALLOW_MOCK";
        private const string WebSearchMockBaseUrlEnv = "KIVRIO_WEB_SEARCH_MOCK_BASE_URL";
        private const string WhisperExeEnv = "KIVRIO_WHISPER_EXE";
        private const string WhisperModelEnv = "KIVRIO_WHISPER_MODEL";
        private const string WhisperLanguageEnv = "KIVRIO_WHISPER_LANGUAGE";
        private const string WhisperTimeoutEnv = "KIVRIO_WHISPER_TIMEOUT_SECONDS";
        private const string WebSearchUnavailableMessage = "La recherche Web est momentan\u00e9ment indisponible. Vous pouvez r\u00e9essayer ou continuer sans recherche Web.";
        private readonly string _root;
        private readonly string _host;
        private readonly int _port;
        private readonly JavaScriptSerializer _json;
        private readonly DataStore _store;
        private readonly string _authPath;
        private readonly bool _authEnabled;
        private readonly bool _sessionCookieSecure;
        private readonly int _sessionTtlSeconds;
        private readonly string _configuredAdminPassword;
        private readonly object _sessionsLock = new object();
        private readonly Dictionary<string, DateTime> _sessions = new Dictionary<string, DateTime>();
        private readonly object _authThrottleLock = new object();
        private readonly Dictionary<string, AuthThrottleRecord> _authThrottle = new Dictionary<string, AuthThrottleRecord>();
        private readonly object _searxngLock = new object();
        private readonly object _shutdownLock = new object();
        private Uri _managedSearxngBaseUri;
        private volatile bool _shutdownRequested;
        private bool _listenerStopQueued;
        private TcpListener _listener;

        private sealed class WhisperConfig
        {
            public string ExecutablePath;
            public string ModelPath;
            public string Language;
            public int TimeoutSeconds;
        }

        public LocalServer(string root, string host, int port)
        {
            _root = root;
            _host = host;
            _port = port;
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            _store = new DataStore(Path.Combine(root, "data"));
            _authPath = Path.Combine(root, "data", "auth.json");
            _authEnabled = !EnvFlag("KIVRO_DISABLE_AUTH", false);
            _sessionCookieSecure = EnvFlag("KIVRO_COOKIE_SECURE", false);
            _sessionTtlSeconds = Math.Max(300, ReadIntEnv("KIVRO_SESSION_TTL_SECONDS", 43200));
            _configuredAdminPassword = (Environment.GetEnvironmentVariable("KIVRO_ADMIN_PASSWORD") ?? "").Trim();
        }

        public void Run()
        {
            IPAddress address;
            if (!IPAddress.TryParse(_host, out address))
            {
                address = IPAddress.Loopback;
            }

            var listener = new TcpListener(address, _port);
            _listener = listener;
            listener.Start();
            try
            {
                while (!_shutdownRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        if (_shutdownRequested) break;
                        throw;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (_shutdownRequested) break;
                        throw;
                    }
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
            }
            finally
            {
                if (ReferenceEquals(_listener, listener))
                {
                    _listener = null;
                }
                try { listener.Stop(); } catch { }
            }
        }

        private void HandleClient(TcpClient client)
        {
            HttpRequest request = null;
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                    NetworkStream stream = client.GetStream();
                    request = HttpRequest.Read(stream);
                    if (request == null)
                    {
                        return;
                    }
                    request.RemoteAddress = RemoteAddressFor(client);

                    HttpResponse response = Route(request);
                    response.Write(stream);
                    QueueListenerStopIfShutdownRequested();
                }
                catch (Exception ex)
                {
                    try
                    {
                        HttpStatusCode status = ErrorResponses.StatusFor(ex);
                        ServerLog.Error("request_error", ErrorLogFields(request, status, ex));
                        JsonError(status, ErrorResponses.MessageFor(ex)).Write(client.GetStream());
                    }
                    catch
                    {
                    }
                }
            }
        }

        private HttpResponse Route(HttpRequest request)
        {
            if (request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                return RouteApi(request);
            }

            return ServeStatic(request);
        }

        private static Dictionary<string, object> ErrorLogFields(HttpRequest request, HttpStatusCode status, Exception ex)
        {
            var fields = new Dictionary<string, object>
            {
                { "app", "kivrio-chat" },
                { "status", (int)status },
                { "exception", ex == null ? "" : ex.GetType().Name },
                { "reason", ErrorResponses.ReasonFor(ex) }
            };
            if (request != null)
            {
                fields["method"] = request.Method;
                fields["path"] = request.Path;
                fields["remote"] = request.RemoteAddress;
            }
            return fields;
        }

        private HttpResponse RouteApi(HttpRequest request)
        {
            string method = request.Method;
            string path = request.Path;

            if (IsUnsafeMethod(method) && !IsAllowedUnsafeRequestOrigin(request))
            {
                return JsonError(HttpStatusCode.Forbidden, "Origine de requete invalide.");
            }

            if (method == "GET" && path == "/api/health")
            {
                return Json(new Dictionary<string, object> { { "ok", true }, { "app", "kivrio-chat" }, { "title", "Kivrio Chat" }, { "store", "json" } });
            }

            if (method == "GET" && path == "/api/auth/status")
            {
                return Json(AuthStatus(IsAuthenticated(request)));
            }

            if (method == "POST" && path == "/api/auth/setup")
            {
                if (!_authEnabled)
                {
                    return Json(AuthStatus(true));
                }
                if (!string.IsNullOrEmpty(_configuredAdminPassword))
                {
                    return JsonError(HttpStatusCode.Conflict, "La creation locale du mot de passe est desactivee quand KIVRO_ADMIN_PASSWORD est defini.");
                }
                if (ReadLocalAuthRecord() != null)
                {
                    return JsonError(HttpStatusCode.Conflict, "Le mot de passe est deja configure.");
                }

                Dictionary<string, object> body = ReadJsonObject(request);
                string password = ValidatePassword(GetBodyString(body, "password"));
                PersistLocalPassword(password);
                string token = CreateSession();
                HttpResponse response = Json(AuthStatus(true));
                response.Headers["Set-Cookie"] = BuildSessionCookie(token, false);
                return response;
            }

            if (method == "POST" && path == "/api/auth/login")
            {
                if (!_authEnabled)
                {
                    return Json(AuthStatus(true));
                }
                if (string.IsNullOrEmpty(_configuredAdminPassword) && ReadLocalAuthRecord() == null)
                {
                    return JsonError(HttpStatusCode.Conflict, "Password setup required.");
                }

                string authThrottleKey = AuthThrottleKey(request);
                int retryAfterSeconds;
                if (IsAuthThrottled(authThrottleKey, out retryAfterSeconds))
                {
                    return AuthThrottleResponse(retryAfterSeconds);
                }

                Dictionary<string, object> body = ReadJsonObject(request);
                if (!VerifyPassword(GetBodyString(body, "password")))
                {
                    retryAfterSeconds = RegisterAuthFailure(authThrottleKey);
                    if (retryAfterSeconds > 0)
                    {
                        return AuthThrottleResponse(retryAfterSeconds);
                    }
                    return JsonError(HttpStatusCode.Unauthorized, "Invalid credentials.");
                }

                ClearAuthFailures(authThrottleKey);
                string token = CreateSession();
                HttpResponse response = Json(AuthStatus(true));
                response.Headers["Set-Cookie"] = BuildSessionCookie(token, false);
                return response;
            }

            if (method == "POST" && path == "/api/auth/logout")
            {
                RevokeSession(GetSessionToken(request));
                Dictionary<string, object> payload = AuthStatus(false);
                payload["ok"] = true;
                HttpResponse response = Json(payload);
                response.Headers["Set-Cookie"] = BuildSessionCookie("", true);
                return response;
            }

            if (method == "POST" && path == "/api/shutdown")
            {
                if (!IsLoopbackRemote(request))
                {
                    return JsonError(HttpStatusCode.Forbidden, "Arret local refuse.");
                }
                if (!IsAuthenticated(request))
                {
                    return JsonError(HttpStatusCode.Unauthorized, "Authentication required.");
                }

                RevokeSession(GetSessionToken(request));
                RequestLocalShutdown();
                Dictionary<string, object> payload = AuthStatus(false);
                payload["ok"] = true;
                payload["shuttingDown"] = true;
                HttpResponse response = Json(payload);
                response.Headers["Set-Cookie"] = BuildSessionCookie("", true);
                return response;
            }

            if (!IsAuthenticated(request))
            {
                return JsonError(HttpStatusCode.Unauthorized, "Authentication required.");
            }

            if (method == "GET" && path == "/api/system-prompt")
            {
                return Json(_store.GetSystemPrompt());
            }
            if (method == "POST" && path == "/api/system-prompt")
            {
                return Json(_store.UpdateSystemPrompt(ReadJsonObject(request)));
            }

            if (method == "POST" && path == "/api/web-search")
            {
                return HandleWebSearch(ReadJsonObject(request));
            }

            if (method == "POST" && path == "/api/voice/transcribe")
            {
                return HandleVoiceTranscription(request);
            }

            if (method == "GET" && path == "/api/conversations")
            {
                return Json(_store.ListConversations());
            }
            if (method == "POST" && path == "/api/conversations")
            {
                return Json(_store.CreateConversation(ReadJsonObject(request)), HttpStatusCode.Created);
            }

            if (method == "GET" && path == "/api/folders")
            {
                return Json(_store.ListFolders());
            }
            if (method == "POST" && path == "/api/folders")
            {
                return Json(_store.CreateFolder(ReadJsonObject(request)), HttpStatusCode.Created);
            }

            if (path.StartsWith("/api/attachments/", StringComparison.OrdinalIgnoreCase))
            {
                return RouteAttachment(request);
            }

            string[] parts = SplitPath(path);
            if (parts.Length >= 3 && parts[0] == "api" && parts[1] == "conversations")
            {
                string conversationId = Uri.UnescapeDataString(parts[2]);
                if (method == "GET" && parts.Length == 3)
                {
                    Dictionary<string, object> item = _store.GetConversationPayload(conversationId);
                    if (item == null) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(item);
                }
                if (method == "PATCH" && parts.Length == 3)
                {
                    Dictionary<string, object> item = _store.UpdateConversation(conversationId, ReadJsonObject(request));
                    if (item == null) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(item);
                }
                if (method == "DELETE" && parts.Length == 3)
                {
                    if (!_store.DeleteConversation(conversationId)) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    CleanupManagedWebSearchRuntimeAfterUserDeletion();
                    return Json(new Dictionary<string, object> { { "ok", true } });
                }
                if (method == "GET" && parts.Length == 4 && parts[3] == "messages")
                {
                    List<Dictionary<string, object>> messages = _store.GetConversationMessages(conversationId);
                    if (messages == null) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(messages);
                }
                if (method == "POST" && parts.Length == 4 && parts[3] == "messages")
                {
                    Dictionary<string, object> message = _store.AddMessage(conversationId, ReadJsonObject(request));
                    if (message == null) return JsonError(HttpStatusCode.NotFound, "Conversation introuvable.");
                    return Json(message, HttpStatusCode.Created);
                }
                if (method == "PATCH" && parts.Length == 5 && parts[3] == "messages")
                {
                    Dictionary<string, object> payload = _store.UpdateMessage(conversationId, Uri.UnescapeDataString(parts[4]), ReadJsonObject(request));
                    if (payload == null) return JsonError(HttpStatusCode.NotFound, "Message introuvable.");
                    return Json(payload);
                }
                if (method == "POST" && parts.Length == 4 && parts[3] == "attachments")
                {
                    List<UploadedFile> files = ReadMultipartFiles(request);
                    return Json(new Dictionary<string, object> { { "attachments", _store.CreateAttachments(conversationId, files) } }, HttpStatusCode.Created);
                }
            }

            if (parts.Length == 3 && parts[0] == "api" && parts[1] == "folders")
            {
                string folderId = Uri.UnescapeDataString(parts[2]);
                if (method == "PATCH")
                {
                    Dictionary<string, object> folder = _store.UpdateFolder(folderId, ReadJsonObject(request));
                    if (folder == null) return JsonError(HttpStatusCode.NotFound, "Dossier introuvable.");
                    return Json(folder);
                }
                if (method == "DELETE")
                {
                    if (!_store.DeleteFolder(folderId)) return JsonError(HttpStatusCode.NotFound, "Dossier introuvable.");
                    return Json(new Dictionary<string, object> { { "ok", true } });
                }
            }

            return JsonError(HttpStatusCode.NotFound, "Endpoint introuvable.");
        }

        private HttpResponse HandleWebSearch(Dictionary<string, object> body)
        {
            string query = GetBodyString(body, "query").Trim();
            if (query.Length == 0 || query.Length > WebSearchQueryMaxLength)
            {
                return JsonError(HttpStatusCode.BadRequest, "Requete Recherche Web invalide.");
            }

            int maxResults = ReadWebSearchMaxResults(body);
            Uri baseUri;
            if (!TryResolveWebSearchBaseUri(out baseUri))
            {
                return Json(WebSearchUnavailablePayload());
            }

            try
            {
                return Json(QueryLocalSearxng(baseUri, query, maxResults));
            }
            catch
            {
                ClearManagedSearxngBaseUri(baseUri);
                return Json(WebSearchUnavailablePayload());
            }
        }

        private bool TryResolveWebSearchBaseUri(out Uri baseUri)
        {
            baseUri = null;
            string configuredBaseUrl = Environment.GetEnvironmentVariable(WebSearchBaseUrlEnv);
            if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
            {
                return TryGetLocalWebSearchBaseUri(configuredBaseUrl, out baseUri);
            }

            lock (_searxngLock)
            {
                if (_managedSearxngBaseUri != null)
                {
                    if (IsWebSearchEndpointHealthy(_managedSearxngBaseUri))
                    {
                        baseUri = _managedSearxngBaseUri;
                        return true;
                    }
                    _managedSearxngBaseUri = null;
                }

                if (TryStartManagedSearxng(out baseUri))
                {
                    _managedSearxngBaseUri = baseUri;
                    return true;
                }
            }

            return false;
        }

        private void ClearManagedSearxngBaseUri(Uri baseUri)
        {
            if (baseUri == null) return;
            lock (_searxngLock)
            {
                if (_managedSearxngBaseUri != null && Uri.Compare(_managedSearxngBaseUri, baseUri, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    _managedSearxngBaseUri = null;
                }
            }
        }

        private void CleanupManagedWebSearchRuntimeAfterUserDeletion()
        {
            StopManagedWebSearchRuntime();
        }

        private void RequestLocalShutdown()
        {
            _shutdownRequested = true;
        }

        private void QueueListenerStopIfShutdownRequested()
        {
            if (!_shutdownRequested)
            {
                return;
            }

            lock (_shutdownLock)
            {
                if (_listenerStopQueued)
                {
                    return;
                }
                _listenerStopQueued = true;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(100);
                    TcpListener listener = _listener;
                    if (listener != null)
                    {
                        listener.Stop();
                    }
                }
                catch
                {
                }
            });
        }

        public void StopManagedWebSearchRuntime()
        {
            lock (_searxngLock)
            {
                _managedSearxngBaseUri = null;
            }

            string pythonPath;
            string stopScriptPath;
            if (TryResolveEmbeddedPythonPath(_root, out pythonPath) && TryResolveSearxngStopScriptPath(_root, out stopScriptPath))
            {
                try
                {
                    var start = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = QuoteArg(stopScriptPath) + " --purge-runtime",
                        WorkingDirectory = Path.GetDirectoryName(stopScriptPath),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (Process process = Process.Start(start))
                    {
                        if (process != null && !process.WaitForExit(SearxngStopTimeoutMs))
                        {
                            try { process.Kill(); } catch { }
                        }
                    }
                }
                catch
                {
                }
            }

            PurgeManagedWebSearchRuntime(_root);
        }

        private bool TryStartManagedSearxng(out Uri baseUri)
        {
            baseUri = null;
            string pythonPath;
            string launcherPath;
            if (!TryResolveEmbeddedPythonPath(_root, out pythonPath)) return false;
            if (!TryResolveSearxngStartScriptPath(_root, out launcherPath)) return false;
            if (EnvFlag(WebSearchAllowMockEnv, false))
            {
                return TryResolveMockLauncherBaseUri(out baseUri);
            }
            if (!IsManagedWebSearchEnabled())
            {
                return false;
            }

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = BuildSearxngLauncherArguments(launcherPath, true),
                    WorkingDirectory = Path.GetDirectoryName(launcherPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.EnvironmentVariables["KIVRIO_SEARXNG_ROOT"] = Path.GetFullPath(Path.Combine(_root, "integrations", "searxng"));

                var output = new StringBuilder();
                var error = new StringBuilder();
                using (var process = new Process())
                {
                    process.StartInfo = start;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                    {
                        if (eventArgs.Data != null) output.AppendLine(eventArgs.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                    {
                        if (eventArgs.Data != null) error.AppendLine(eventArgs.Data);
                    };
                    if (!process.Start()) return false;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(SearxngLauncherTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }
                    process.WaitForExit();
                    return TryReadLauncherBaseUri(output.ToString(), out baseUri);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveMockLauncherBaseUri(out Uri baseUri)
        {
            baseUri = null;
            string mockBaseUrl = Environment.GetEnvironmentVariable(WebSearchMockBaseUrlEnv);
            if (string.IsNullOrWhiteSpace(mockBaseUrl)) return false;
            return TryGetLocalWebSearchBaseUri(mockBaseUrl, out baseUri);
        }

        internal static bool IsManagedWebSearchEnabled()
        {
            return EnvFlag(WebSearchEnableManagedEnv, true);
        }

        internal static string BuildSearxngLauncherArguments(string launcherPath, bool realStart)
        {
            string arguments = QuoteArg(launcherPath) + " --json";
            if (realStart)
            {
                arguments += " --real-start";
            }
            return arguments;
        }

        internal static bool TryResolveEmbeddedPythonPath(string root, out string pythonPath)
        {
            pythonPath = null;
            if (string.IsNullOrWhiteSpace(root)) return false;
            string rootPath = Path.GetFullPath(root);
            string candidate = Path.GetFullPath(Path.Combine(rootPath, "runtime", "python", "python.exe"));
            if (!IsPathInsideDirectory(Path.Combine(rootPath, "runtime", "python"), candidate)) return false;
            if (!File.Exists(candidate)) return false;
            pythonPath = candidate;
            return true;
        }

        internal static bool TryResolveSearxngStartScriptPath(string root, out string launcherPath)
        {
            launcherPath = null;
            if (string.IsNullOrWhiteSpace(root)) return false;
            string rootPath = Path.GetFullPath(root);
            string searxngRoot = Path.Combine(rootPath, "integrations", "searxng");
            string candidate = Path.GetFullPath(Path.Combine(searxngRoot, "launcher", "start_searxng.py"));
            if (!IsPathInsideDirectory(searxngRoot, candidate)) return false;
            if (!File.Exists(candidate)) return false;
            launcherPath = candidate;
            return true;
        }

        internal static bool TryResolveSearxngStopScriptPath(string root, out string stopScriptPath)
        {
            stopScriptPath = null;
            if (string.IsNullOrWhiteSpace(root)) return false;
            string rootPath = Path.GetFullPath(root);
            string searxngRoot = Path.Combine(rootPath, "integrations", "searxng");
            string candidate = Path.GetFullPath(Path.Combine(searxngRoot, "launcher", "stop_searxng.py"));
            if (!IsPathInsideDirectory(searxngRoot, candidate)) return false;
            if (!File.Exists(candidate)) return false;
            stopScriptPath = candidate;
            return true;
        }

        internal static void PurgeManagedWebSearchRuntime(string root)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                string rootPath = Path.GetFullPath(root);
                string searxngRoot = Path.Combine(rootPath, "integrations", "searxng");
                string runtimeDir = Path.GetFullPath(Path.Combine(searxngRoot, "runtime"));
                if (!IsPathInsideDirectory(searxngRoot, runtimeDir)) return;
                if (!Directory.Exists(runtimeDir)) return;

                DeleteRuntimeFile(runtimeDir, "searxng.pid");
                DeleteRuntimeFile(runtimeDir, "searxng.stdout.log");
                DeleteRuntimeFile(runtimeDir, "searxng.stderr.log");
                DeleteRuntimeFile(runtimeDir, "settings-launch.yml");
                ClearRuntimeDirectory(runtimeDir, "cache");
                ClearRuntimeDirectory(runtimeDir, "logs");
                ClearRuntimeDirectory(runtimeDir, "tmp");
            }
            catch
            {
            }
        }

        private static void DeleteRuntimeFile(string runtimeDir, string fileName)
        {
            try
            {
                string path = Path.GetFullPath(Path.Combine(runtimeDir, fileName));
                if (!IsPathInsideDirectory(runtimeDir, path)) return;
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        private static void ClearRuntimeDirectory(string runtimeDir, string directoryName)
        {
            try
            {
                string directory = Path.GetFullPath(Path.Combine(runtimeDir, directoryName));
                if (!IsPathInsideDirectory(runtimeDir, directory)) return;
                if (!Directory.Exists(directory)) return;
                foreach (string file in Directory.GetFiles(directory))
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(file);
                        if (IsPathInsideDirectory(directory, fullPath)) File.Delete(fullPath);
                    }
                    catch
                    {
                    }
                }
                foreach (string childDirectory in Directory.GetDirectories(directory))
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(childDirectory);
                        if (IsPathInsideDirectory(directory, fullPath)) Directory.Delete(fullPath, true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        internal static bool TryReadLauncherBaseUri(string output, out Uri baseUri)
        {
            baseUri = null;
            string text = (output ?? "").Trim();
            if (text.Length == 0) return false;

            try
            {
                object parsed = new JavaScriptSerializer().DeserializeObject(text);
                var payload = parsed as Dictionary<string, object>;
                if (payload == null) return false;

                object okValue;
                bool ok = payload.TryGetValue("ok", out okValue) && Convert.ToString(okValue).Equals("True", StringComparison.OrdinalIgnoreCase);
                if (!ok) return false;

                object baseUrlValue;
                if (!payload.TryGetValue("base_url", out baseUrlValue) || baseUrlValue == null) return false;
                return TryGetLocalWebSearchBaseUri(Convert.ToString(baseUrlValue), out baseUri);
            }
            catch
            {
                return false;
            }
        }

        private static string QuoteArg(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static Dictionary<string, object> WebSearchUnavailablePayload()
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "available", false },
                { "results", new List<object>() },
                { "message", WebSearchUnavailableMessage }
            };
        }

        private Dictionary<string, object> QueryLocalSearxng(Uri baseUri, string query, int maxResults)
        {
            Uri searchUri = BuildSearxngSearchUri(baseUri, query, maxResults);
            var request = (HttpWebRequest)WebRequest.Create(searchUri);
            request.Method = "GET";
            request.Accept = "application/json";
            request.Timeout = WebSearchTimeoutMs;
            request.ReadWriteTimeout = WebSearchTimeoutMs;

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return WebSearchUnavailablePayload();
                }

                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    object parsed = _json.DeserializeObject(reader.ReadToEnd());
                    var payload = parsed as Dictionary<string, object>;
                    return new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "available", true },
                        { "results", NormalizeWebSearchResults(payload, maxResults) },
                        { "message", "" }
                    };
                }
            }
        }

        private static Uri BuildSearxngSearchUri(Uri baseUri, string query, int maxResults)
        {
            string root = baseUri.ToString().TrimEnd('/') + "/";
            Uri searchRoot = new Uri(new Uri(root), "search");
            string queryString = "q=" + Uri.EscapeDataString(query)
                + "&format=json"
                + "&language=auto"
                + "&safesearch=0"
                + "&pageno=1"
                + "&categories=general"
                + "&max_results=" + Math.Max(1, Math.Min(maxResults, WebSearchMaxResults));
            return new Uri(searchRoot.ToString() + "?" + queryString);
        }

        private static bool IsWebSearchEndpointHealthy(Uri baseUri)
        {
            if (baseUri == null) return false;
            try
            {
                Uri healthUri = new Uri(baseUri.ToString().TrimEnd('/') + "/healthz");
                var request = (HttpWebRequest)WebRequest.Create(healthUri);
                request.Method = "GET";
                request.Accept = "text/plain, application/json";
                request.Timeout = WebSearchHealthcheckTimeoutMs;
                request.ReadWriteTimeout = WebSearchHealthcheckTimeoutMs;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetLocalWebSearchBaseUri(string rawBaseUrl, out Uri baseUri)
        {
            baseUri = null;
            string value = (rawBaseUrl ?? "").Trim();
            if (value.Length == 0) return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out baseUri)) return false;
            if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(baseUri.UserInfo)) return false;
            return IsLoopbackHost(baseUri.Host);
        }

        private static bool IsLoopbackHost(string host)
        {
            string value = (host ?? "").Trim().Trim('[', ']').ToLowerInvariant();
            if (value == "localhost") return true;
            IPAddress address;
            return IPAddress.TryParse(value, out address) && IPAddress.IsLoopback(address);
        }

        private static int ReadWebSearchMaxResults(Dictionary<string, object> body)
        {
            int parsed;
            string raw = GetBodyString(body, "max_results");
            if (raw.Length == 0)
            {
                raw = GetBodyString(body, "maxResults");
            }
            if (!int.TryParse(raw, out parsed))
            {
                parsed = WebSearchDefaultMaxResults;
            }
            return Math.Max(1, Math.Min(parsed, WebSearchMaxResults));
        }

        private static List<Dictionary<string, object>> NormalizeWebSearchResults(Dictionary<string, object> payload, int maxResults)
        {
            var output = new List<Dictionary<string, object>>();
            if (payload == null) return output;

            object rawResults;
            if (!payload.TryGetValue("results", out rawResults)) return output;

            object[] items = rawResults as object[];
            if (items == null) return output;

            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object rawItem in items)
            {
                var item = rawItem as Dictionary<string, object>;
                if (item == null) continue;

                string title = CleanWebText(WebSearchStringValue(item, "title"), 180);
                string url = NormalizeWebSearchUrl(WebSearchStringValue(item, "url"));
                string snippet = CleanWebText(FirstWebSearchStringValue(item, "content", "snippet"), 500);
                string source = CleanWebText(FirstWebSearchStringValue(item, "engine", "source"), 120);
                if (source.Length == 0) source = HostFromUrl(url);
                if (title.Length == 0 || url.Length == 0) continue;
                if (seenUrls.Contains(url)) continue;

                seenUrls.Add(url);
                output.Add(new Dictionary<string, object>
                {
                    { "title", title },
                    { "url", url },
                    { "snippet", snippet },
                    { "source", source }
                });
                if (output.Count >= maxResults) break;
            }

            return output;
        }

        private static string WebSearchStringValue(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value) ?? "";
        }

        private static string FirstWebSearchStringValue(Dictionary<string, object> item, params string[] keys)
        {
            foreach (string key in keys ?? new string[0])
            {
                string value = WebSearchStringValue(item, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static string NormalizeWebSearchUrl(string raw)
        {
            Uri uri;
            if (!Uri.TryCreate((raw ?? "").Trim(), UriKind.Absolute, out uri)) return "";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
            return uri.ToString();
        }

        private static string HostFromUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "";
            return uri.Host;
        }

        private static string CleanWebText(string raw, int maxLength)
        {
            string decoded = WebUtility.HtmlDecode(raw ?? "");
            var builder = new StringBuilder();
            bool insideTag = false;
            foreach (char ch in decoded)
            {
                if (ch == '<')
                {
                    insideTag = true;
                    continue;
                }
                if (ch == '>')
                {
                    insideTag = false;
                    continue;
                }
                if (!insideTag)
                {
                    builder.Append(ch);
                }
            }
            return LimitText(CollapseWhitespace(builder.ToString()), maxLength);
        }

        private static string CollapseWhitespace(string value)
        {
            var builder = new StringBuilder();
            bool previousWasWhitespace = false;
            foreach (char ch in value ?? "")
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!previousWasWhitespace)
                    {
                        builder.Append(' ');
                        previousWasWhitespace = true;
                    }
                    continue;
                }
                builder.Append(ch);
                previousWasWhitespace = false;
            }
            return builder.ToString().Trim();
        }

        private static string LimitText(string value, int maxLength)
        {
            string text = (value ?? "").Trim();
            if (maxLength <= 0 || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength).Trim();
        }

        private HttpResponse HandleVoiceTranscription(HttpRequest request)
        {
            List<UploadedFile> files = ReadMultipartFiles(request);
            if (files.Count == 0)
            {
                return JsonError(HttpStatusCode.BadRequest, "Aucun audio de dictee recu.");
            }

            UploadedFile audio = files[0];
            string validationError = ValidateVoiceAudio(audio);
            if (!string.IsNullOrEmpty(validationError))
            {
                return JsonError(HttpStatusCode.BadRequest, validationError);
            }

            WhisperConfig config = ReadWhisperConfig();
            string configError = ValidateWhisperConfig(config);
            if (!string.IsNullOrEmpty(configError))
            {
                return JsonError(HttpStatusCode.ServiceUnavailable, configError);
            }

            string text;
            try
            {
                text = RunWhisperTranscription(audio.Content, config);
            }
            catch (InvalidOperationException ex)
            {
                return JsonError(HttpStatusCode.ServiceUnavailable, ex.Message);
            }
            return Json(new Dictionary<string, object>
            {
                { "text", text },
                { "language", config.Language }
            });
        }

        private static string ValidateVoiceAudio(UploadedFile audio)
        {
            if (audio == null || audio.Content == null || audio.Content.Length == 0)
            {
                return "Audio de dictee vide.";
            }
            if (audio.Content.Length > VoiceAudioMaxBytes)
            {
                return "Audio de dictee trop volumineux.";
            }
            if (!LooksLikeWav(audio.Content))
            {
                return "Format audio de dictee non pris en charge.";
            }
            return "";
        }

        private static bool LooksLikeWav(byte[] content)
        {
            return content != null
                && content.Length > 44
                && StartsWithAscii(content, 0, "RIFF")
                && StartsWithAscii(content, 8, "WAVE");
        }

        private static bool StartsWithAscii(byte[] content, int offset, string value)
        {
            if (content == null || value == null || offset < 0 || content.Length < offset + value.Length)
            {
                return false;
            }
            for (int i = 0; i < value.Length; i++)
            {
                if (content[offset + i] != (byte)value[i])
                {
                    return false;
                }
            }
            return true;
        }

        private WhisperConfig ReadWhisperConfig()
        {
            Dictionary<string, object> fileConfig = ReadWhisperConfigFile();
            string exe = FirstNonEmpty(
                Environment.GetEnvironmentVariable(WhisperExeEnv),
                ConfigString(fileConfig, "executablePath"),
                "integrations/whisper/bin/whisper-cli.exe");
            string model = FirstNonEmpty(
                Environment.GetEnvironmentVariable(WhisperModelEnv),
                ConfigString(fileConfig, "modelPath"),
                "integrations/whisper/models/ggml-base.bin");
            string language = FirstNonEmpty(
                Environment.GetEnvironmentVariable(WhisperLanguageEnv),
                ConfigString(fileConfig, "language"),
                "fr");
            int timeoutSeconds = ClampTimeoutSeconds(FirstNonEmpty(
                Environment.GetEnvironmentVariable(WhisperTimeoutEnv),
                ConfigString(fileConfig, "timeoutSeconds"),
                Convert.ToString(VoiceDefaultTimeoutSeconds)));

            return new WhisperConfig
            {
                ExecutablePath = ResolveLocalWhisperPath(exe),
                ModelPath = ResolveLocalWhisperPath(model),
                Language = CleanWhisperLanguage(language),
                TimeoutSeconds = timeoutSeconds
            };
        }

        private Dictionary<string, object> ReadWhisperConfigFile()
        {
            string configPath = Path.GetFullPath(Path.Combine(_root, "integrations", "whisper", "config.json"));
            string integrationRoot = Path.GetFullPath(Path.Combine(_root, "integrations", "whisper"));
            if (!IsPathInsideDirectory(integrationRoot, configPath) || !File.Exists(configPath))
            {
                return new Dictionary<string, object>();
            }
            try
            {
                object parsed = _json.DeserializeObject(File.ReadAllText(configPath, Encoding.UTF8));
                return parsed as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private string ResolveLocalWhisperPath(string value)
        {
            string raw = (value ?? "").Trim().Replace('/', Path.DirectorySeparatorChar);
            if (raw.Length == 0) return "";
            raw = Environment.ExpandEnvironmentVariables(raw);
            string candidate = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(_root, raw));
            return IsPathInsideDirectory(_root, candidate) ? candidate : "";
        }

        private static string ValidateWhisperConfig(WhisperConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.ExecutablePath) || !File.Exists(config.ExecutablePath))
            {
                return "Dictee vocale non configuree: executable whisper.cpp introuvable.";
            }
            if (string.IsNullOrWhiteSpace(config.ModelPath) || !File.Exists(config.ModelPath))
            {
                return "Dictee vocale non configuree: modele Whisper introuvable.";
            }
            return "";
        }

        private string RunWhisperTranscription(byte[] audioBytes, WhisperConfig config)
        {
            string tempRoot = Path.GetFullPath(Path.Combine(_root, "data", "voice-tmp"));
            if (!IsPathInsideDirectory(Path.Combine(_root, "data"), tempRoot))
            {
                throw new InvalidOperationException("Dossier temporaire de dictee invalide.");
            }
            Directory.CreateDirectory(tempRoot);

            string id = GenerateToken(8);
            string inputPath = Path.Combine(tempRoot, "voice-" + id + ".wav");
            string outputBase = Path.Combine(tempRoot, "voice-" + id + "-out");
            string outputTextPath = outputBase + ".txt";

            try
            {
                File.WriteAllBytes(inputPath, audioBytes ?? new byte[0]);
                var start = new ProcessStartInfo
                {
                    FileName = config.ExecutablePath,
                    Arguments = BuildWhisperArguments(config, inputPath, outputBase),
                    WorkingDirectory = Path.GetDirectoryName(config.ExecutablePath),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        throw new InvalidOperationException("Demarrage de whisper.cpp impossible.");
                    }
                    if (!process.WaitForExit(Math.Max(1, config.TimeoutSeconds) * 1000))
                    {
                        try { process.Kill(); } catch { }
                        throw new InvalidOperationException("Delai de transcription depasse.");
                    }
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException("Transcription vocale impossible.");
                    }
                }

                string text = File.Exists(outputTextPath)
                    ? File.ReadAllText(outputTextPath, Encoding.UTF8)
                    : "";
                text = CollapseWhitespace(text);
                if (text.Length == 0)
                {
                    throw new InvalidOperationException("Aucun texte reconnu.");
                }
                return text;
            }
            finally
            {
                TryDeleteLocalFile(inputPath);
                TryDeleteLocalFile(outputTextPath);
            }
        }

        private static string BuildWhisperArguments(WhisperConfig config, string inputPath, string outputBase)
        {
            string arguments = "-m " + QuoteArg(config.ModelPath)
                + " -f " + QuoteArg(inputPath)
                + " -nt -np -otxt -of " + QuoteArg(outputBase);
            if (!string.IsNullOrWhiteSpace(config.Language))
            {
                arguments += " -l " + QuoteArg(config.Language);
            }
            return arguments;
        }

        private static void TryDeleteLocalFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string ConfigString(Dictionary<string, object> config, string key)
        {
            object value;
            if (config != null && config.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToString(value);
            }
            return "";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return "";
        }

        private static int ClampTimeoutSeconds(string value)
        {
            int parsed;
            if (!int.TryParse(value, out parsed)) parsed = VoiceDefaultTimeoutSeconds;
            return Math.Max(5, Math.Min(VoiceMaxTimeoutSeconds, parsed));
        }

        private static string CleanWhisperLanguage(string value)
        {
            string text = (value ?? "").Trim().ToLowerInvariant();
            if (text == "auto") return "";
            if (text.Length > 12) return "fr";
            foreach (char ch in text)
            {
                bool ok = (ch >= 'a' && ch <= 'z') || ch == '-' || ch == '_';
                if (!ok) return "fr";
            }
            return text.Length == 0 ? "fr" : text;
        }

        private HttpResponse RouteAttachment(HttpRequest request)
        {
            string[] parts = SplitPath(request.Path);
            if (request.Method != "GET" || parts.Length != 4 || parts[0] != "api" || parts[1] != "attachments")
            {
                return JsonError(HttpStatusCode.NotFound, "Piece jointe introuvable.");
            }

            AttachmentRecord attachment = _store.GetAttachment(Uri.UnescapeDataString(parts[2]));
            if (attachment == null)
            {
                return JsonError(HttpStatusCode.NotFound, "Piece jointe introuvable.");
            }

            string filePath = _store.GetAttachmentPath(attachment);
            if (!File.Exists(filePath))
            {
                return JsonError(HttpStatusCode.NotFound, "Fichier joint introuvable.");
            }

            if (parts[3] == "view")
            {
                if (!IsImageAttachment(attachment))
                {
                    return JsonError(HttpStatusCode.BadRequest, "Apercu disponible uniquement pour les images.");
                }

                string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>" +
                    HtmlEscape(attachment.filename) +
                    "</title><style>body{margin:0;background:#0f172a;display:grid;place-items:center;min-height:100vh}img{max-width:96vw;max-height:96vh;background:white}</style></head><body><img src=\"/api/attachments/" +
                    Uri.EscapeDataString(attachment.id) +
                    "/content\" alt=\"" +
                    HtmlEscape(attachment.filename) +
                    "\"></body></html>";
                return new HttpResponse(HttpStatusCode.OK, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            }

            if (parts[3] == "content")
            {
                HttpResponse response = new HttpResponse(HttpStatusCode.OK, AttachmentContentType(attachment), File.ReadAllBytes(filePath));
                response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                if (!IsImageAttachment(attachment))
                {
                    response.Headers["Content-Disposition"] = BuildAttachmentContentDisposition(attachment.filename);
                    response.Headers["X-Download-Options"] = "noopen";
                }
                return response;
            }

            if (parts[3] == "text")
            {
                return RouteAttachmentText(attachment, filePath);
            }

            return JsonError(HttpStatusCode.NotFound, "Piece jointe introuvable.");
        }

        private HttpResponse RouteAttachmentText(AttachmentRecord attachment, string filePath)
        {
            if (!IsPdfAttachment(attachment))
            {
                return JsonError(HttpStatusCode.BadRequest, "Extraction texte disponible uniquement pour les PDF.");
            }

            try
            {
                PdfTextExtractionResult result = PdfTextExtractor.ExtractText(filePath, PdfExtractedTextMaxChars);
                return Json(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "attachmentId", attachment.id },
                    { "filename", attachment.filename },
                    { "pageCount", result.PageCount },
                    { "text", result.Text ?? "" },
                    { "truncated", result.Truncated }
                });
            }
            catch
            {
                return JsonError(HttpStatusCode.BadRequest, "Lecture PDF impossible.");
            }
        }

        private static bool IsImageAttachment(AttachmentRecord attachment)
        {
            return (attachment == null ? "" : (attachment.mimeType ?? "")).StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPdfAttachment(AttachmentRecord attachment)
        {
            string mime = attachment == null ? "" : NormalizeAttachmentMime(attachment.mimeType);
            string ext = Path.GetExtension(attachment == null ? "" : (attachment.filename ?? "")).ToLowerInvariant();
            return ext == ".pdf" || mime == "application/pdf";
        }

        private static string AttachmentContentType(AttachmentRecord attachment)
        {
            string ext = Path.GetExtension(attachment == null ? "" : (attachment.filename ?? "")).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            if (ext == ".png") return "image/png";
            if (ext == ".webp") return "image/webp";
            if (ext == ".pdf") return "application/pdf";
            if (ext == ".txt" || ext == ".md") return "text/plain; charset=utf-8";
            return "application/octet-stream";
        }

        private static string NormalizeAttachmentMime(string contentType)
        {
            string mime = (contentType ?? "").Trim().ToLowerInvariant();
            int semi = mime.IndexOf(';');
            if (semi >= 0) mime = mime.Substring(0, semi).Trim();
            return mime;
        }

        private static string BuildAttachmentContentDisposition(string filename)
        {
            return "attachment; filename=\"" + SafeHeaderFileName(filename) + "\"";
        }

        private static string SafeHeaderFileName(string filename)
        {
            string name = Path.GetFileName(filename ?? "attachment");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "attachment";
            }

            var builder = new StringBuilder();
            foreach (char ch in name)
            {
                if (ch <= 31 || ch >= 127 || ch == '"' || ch == '\\')
                {
                    builder.Append('_');
                    continue;
                }
                builder.Append(ch);
            }
            return builder.ToString();
        }

        private HttpResponse ServeStatic(HttpRequest request)
        {
            if (request.Method != "GET" && request.Method != "HEAD")
            {
                return JsonError(HttpStatusCode.MethodNotAllowed, "Method not allowed.");
            }

            string path = request.Path;
            if (path == "/")
            {
                path = "/index.html";
            }

            string fullPath = ResolvePublicStaticPath(path);
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                return JsonError(HttpStatusCode.NotFound, "Resource not found.");
            }

            byte[] body = request.Method == "HEAD" ? new byte[0] : File.ReadAllBytes(fullPath);
            HttpResponse response = new HttpResponse(HttpStatusCode.OK, MimeTypeFor(fullPath), body);
            response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            return response;
        }

        private string ResolvePublicStaticPath(string path)
        {
            if (string.IsNullOrEmpty(path) || HasDotSegment(path))
            {
                return null;
            }

            string relative;
            if (path == "/index.html" || path == "/favicon.ico")
            {
                relative = path.TrimStart('/');
            }
            else if (path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            {
                relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            }
            else
            {
                return null;
            }

            string fullPath = Path.GetFullPath(Path.Combine(_root, relative));
            if (!IsAllowedStaticFile(fullPath))
            {
                return null;
            }
            return fullPath;
        }

        private bool IsAllowedStaticFile(string fullPath)
        {
            return string.Equals(fullPath, Path.GetFullPath(Path.Combine(_root, "index.html")), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, Path.GetFullPath(Path.Combine(_root, "favicon.ico")), StringComparison.OrdinalIgnoreCase)
                || IsPathInsideDirectory(Path.Combine(_root, "css"), fullPath)
                || IsPathInsideDirectory(Path.Combine(_root, "js"), fullPath)
                || IsPathInsideDirectory(Path.Combine(_root, "assets"), fullPath);
        }

        private static bool HasDotSegment(string path)
        {
            string[] segments = (path ?? "").Replace('\\', '/').Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == "." || segments[i] == "..")
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPathInsideDirectory(string rootDir, string candidatePath)
        {
            if (string.IsNullOrEmpty(rootDir) || string.IsNullOrEmpty(candidatePath)) return false;
            string root = Path.GetFullPath(rootDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(candidatePath);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private Dictionary<string, object> ReadJsonObject(HttpRequest request)
        {
            if (request.Body == null || request.Body.Length == 0)
            {
                return new Dictionary<string, object>();
            }

            object parsed = _json.DeserializeObject(Encoding.UTF8.GetString(request.Body));
            var dict = parsed as Dictionary<string, object>;
            return dict ?? new Dictionary<string, object>();
        }

        private List<UploadedFile> ReadMultipartFiles(HttpRequest request)
        {
            string contentType = request.GetHeader("Content-Type") ?? "";
            string boundary = ExtractBoundary(contentType);
            if (string.IsNullOrEmpty(boundary))
            {
                throw new InvalidOperationException("Boundary multipart introuvable.");
            }
            if (!MultipartParser.IsSafeBoundary(boundary))
            {
                throw new InvalidOperationException("Boundary multipart invalide.");
            }
            return MultipartParser.Parse(request.Body ?? new byte[0], boundary);
        }

        private HttpResponse Json(object payload)
        {
            return Json(payload, HttpStatusCode.OK);
        }

        private HttpResponse Json(object payload, HttpStatusCode status)
        {
            byte[] body = Encoding.UTF8.GetBytes(_json.Serialize(payload));
            var response = new HttpResponse(status, "application/json; charset=utf-8", body);
            response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            return response;
        }

        private HttpResponse JsonError(HttpStatusCode status, string message)
        {
            return Json(new Dictionary<string, object> { { "error", message } }, status);
        }

        private HttpResponse AuthThrottleResponse(int retryAfterSeconds)
        {
            HttpResponse response = JsonError((HttpStatusCode)429, "Trop de tentatives. Reessayez plus tard.");
            response.Headers["Retry-After"] = Math.Max(1, retryAfterSeconds).ToString();
            return response;
        }

        private bool IsAuthThrottled(string key, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            DateTime now = DateTime.UtcNow;
            lock (_authThrottleLock)
            {
                PurgeAuthThrottleLocked(now);
                AuthThrottleRecord record;
                if (!_authThrottle.TryGetValue(key, out record) || record.LockedUntilUtc <= now)
                {
                    return false;
                }

                retryAfterSeconds = SecondsUntil(record.LockedUntilUtc, now);
                return true;
            }
        }

        private int RegisterAuthFailure(string key)
        {
            DateTime now = DateTime.UtcNow;
            lock (_authThrottleLock)
            {
                PurgeAuthThrottleLocked(now);
                AuthThrottleRecord record;
                if (!_authThrottle.TryGetValue(key, out record))
                {
                    record = new AuthThrottleRecord();
                    _authThrottle[key] = record;
                }

                if (record.FirstFailureUtc == DateTime.MinValue
                    || now > record.FirstFailureUtc.AddSeconds(AuthFailureWindowSeconds))
                {
                    record.FirstFailureUtc = now;
                    record.Failures = 0;
                    record.LockedUntilUtc = DateTime.MinValue;
                }

                record.Failures++;
                if (record.Failures >= MaxAuthFailures)
                {
                    record.LockedUntilUtc = now.AddSeconds(AuthLockoutSeconds);
                    return AuthLockoutSeconds;
                }

                return 0;
            }
        }

        private void ClearAuthFailures(string key)
        {
            lock (_authThrottleLock)
            {
                _authThrottle.Remove(key);
            }
        }

        private void PurgeAuthThrottleLocked(DateTime now)
        {
            var expired = new List<string>();
            foreach (var pair in _authThrottle)
            {
                AuthThrottleRecord record = pair.Value;
                bool windowExpired = record.FirstFailureUtc == DateTime.MinValue
                    || now > record.FirstFailureUtc.AddSeconds(AuthFailureWindowSeconds);
                bool lockExpired = record.LockedUntilUtc <= now;
                if (windowExpired && lockExpired)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (string key in expired)
            {
                _authThrottle.Remove(key);
            }
        }

        private static string AuthThrottleKey(HttpRequest request)
        {
            string remote = request == null ? "" : (request.RemoteAddress ?? "");
            remote = NormalizeHost(remote);
            return remote.Length == 0 ? "local" : remote;
        }

        private static int SecondsUntil(DateTime deadlineUtc, DateTime nowUtc)
        {
            return Math.Max(1, (int)Math.Ceiling((deadlineUtc - nowUtc).TotalSeconds));
        }

        private bool IsAllowedUnsafeRequestOrigin(HttpRequest request)
        {
            string origin = request.GetHeader("Origin");
            if (!string.IsNullOrWhiteSpace(origin))
            {
                return MatchesRequestHost(request, origin);
            }

            string referer = request.GetHeader("Referer");
            if (!string.IsNullOrWhiteSpace(referer))
            {
                return MatchesRequestHost(request, referer);
            }

            return true;
        }

        private static bool IsLoopbackRemote(HttpRequest request)
        {
            string remote = request == null ? "" : (request.RemoteAddress ?? "");
            remote = NormalizeHost(remote);
            if (remote.Length == 0)
            {
                return false;
            }

            if (string.Equals(remote, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            IPAddress address;
            return IPAddress.TryParse(remote, out address) && IPAddress.IsLoopback(address);
        }

        private bool MatchesRequestHost(HttpRequest request, string rawUri)
        {
            Uri uri;
            if (!Uri.TryCreate((rawUri ?? "").Trim(), UriKind.Absolute, out uri))
            {
                return false;
            }
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string hostHeader = (request.GetHeader("Host") ?? "").Trim();
            if (string.IsNullOrEmpty(hostHeader) && _port > 0)
            {
                hostHeader = _host + ":" + _port;
            }

            string expectedHost;
            int? expectedPort;
            if (!TryParseHostHeader(hostHeader, out expectedHost, out expectedPort))
            {
                return false;
            }

            if (!string.Equals(NormalizeHost(expectedHost), NormalizeHost(uri.Host), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int actualPort = uri.IsDefaultPort ? DefaultPortForScheme(uri.Scheme) : uri.Port;
            if (expectedPort.HasValue)
            {
                return expectedPort.Value == actualPort;
            }
            return uri.IsDefaultPort;
        }

        private static bool IsUnsafeMethod(string method)
        {
            return !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseHostHeader(string value, out string host, out int? port)
        {
            host = null;
            port = null;

            string raw = (value ?? "").Trim();
            if (raw.Length == 0)
            {
                return false;
            }

            if (raw[0] == '[')
            {
                int end = raw.IndexOf(']');
                if (end <= 0)
                {
                    return false;
                }

                host = raw.Substring(1, end - 1);
                if (raw.Length > end + 1)
                {
                    if (raw[end + 1] != ':')
                    {
                        return false;
                    }
                    int parsedPort;
                    if (!int.TryParse(raw.Substring(end + 2), out parsedPort) || parsedPort <= 0 || parsedPort > 65535)
                    {
                        return false;
                    }
                    port = parsedPort;
                }
                return NormalizeHost(host).Length > 0;
            }

            int firstColon = raw.IndexOf(':');
            int lastColon = raw.LastIndexOf(':');
            if (firstColon > 0 && firstColon == lastColon)
            {
                int parsedPort;
                if (!int.TryParse(raw.Substring(lastColon + 1), out parsedPort) || parsedPort <= 0 || parsedPort > 65535)
                {
                    return false;
                }
                host = raw.Substring(0, lastColon);
                port = parsedPort;
            }
            else
            {
                host = raw;
            }

            return NormalizeHost(host).Length > 0;
        }

        private static string NormalizeHost(string host)
        {
            return (host ?? "")
                .Trim()
                .Trim('[', ']')
                .TrimEnd('.')
                .ToLowerInvariant();
        }

        private static int DefaultPortForScheme(string scheme)
        {
            return string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        }

        private static string RemoteAddressFor(TcpClient client)
        {
            try
            {
                IPEndPoint endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                return endpoint == null ? "" : endpoint.Address.ToString();
            }
            catch
            {
                return "";
            }
        }

        private Dictionary<string, object> AuthStatus(bool authenticated)
        {
            bool setupRequired = _authEnabled && string.IsNullOrEmpty(_configuredAdminPassword) && ReadLocalAuthRecord() == null;
            string passwordSource = !_authEnabled
                ? "disabled"
                : !string.IsNullOrEmpty(_configuredAdminPassword)
                    ? "environment"
                    : setupRequired
                        ? "unconfigured"
                        : "local";

            return new Dictionary<string, object>
            {
                { "enabled", _authEnabled },
                { "authenticated", !_authEnabled || authenticated },
                { "setupRequired", setupRequired },
                { "passwordSource", passwordSource },
                { "ok", true }
            };
        }

        private bool IsAuthenticated(HttpRequest request)
        {
            if (!_authEnabled)
            {
                return true;
            }

            string token = GetSessionToken(request);
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            PurgeExpiredSessions();
            lock (_sessionsLock)
            {
                DateTime expiresAt;
                if (!_sessions.TryGetValue(token, out expiresAt) || expiresAt <= DateTime.UtcNow)
                {
                    _sessions.Remove(token);
                    return false;
                }

                _sessions[token] = DateTime.UtcNow.AddSeconds(_sessionTtlSeconds);
                return true;
            }
        }

        private string GetSessionToken(HttpRequest request)
        {
            string raw = request.GetHeader("Cookie") ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string[] parts = raw.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                int index = part.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                string name = part.Substring(0, index).Trim();
                if (string.Equals(name, SessionCookieName, StringComparison.Ordinal))
                {
                    return part.Substring(index + 1).Trim();
                }
            }

            return null;
        }

        private string CreateSession()
        {
            PurgeExpiredSessions();
            string token = GenerateToken(32);
            lock (_sessionsLock)
            {
                _sessions[token] = DateTime.UtcNow.AddSeconds(_sessionTtlSeconds);
            }
            return token;
        }

        private void RevokeSession(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return;
            }

            lock (_sessionsLock)
            {
                _sessions.Remove(token);
            }
        }

        private void PurgeExpiredSessions()
        {
            lock (_sessionsLock)
            {
                var expired = new List<string>();
                foreach (var pair in _sessions)
                {
                    if (pair.Value <= DateTime.UtcNow)
                    {
                        expired.Add(pair.Key);
                    }
                }
                foreach (string token in expired)
                {
                    _sessions.Remove(token);
                }
            }
        }

        private string BuildSessionCookie(string token, bool clear)
        {
            var parts = new List<string>
            {
                SessionCookieName + "=" + token,
                "Path=/",
                "HttpOnly",
                "SameSite=Lax",
                clear ? "Max-Age=0" : "Max-Age=" + _sessionTtlSeconds
            };
            if (clear)
            {
                parts.Add("Expires=Thu, 01 Jan 1970 00:00:00 GMT");
            }
            if (_sessionCookieSecure)
            {
                parts.Add("Secure");
            }
            return string.Join("; ", parts.ToArray());
        }

        private Dictionary<string, object> ReadLocalAuthRecord()
        {
            if (!File.Exists(_authPath))
            {
                return null;
            }

            try
            {
                object parsed = _json.DeserializeObject(File.ReadAllText(_authPath, Encoding.UTF8));
                var record = parsed as Dictionary<string, object>;
                if (record == null
                    || !record.ContainsKey("salt")
                    || !record.ContainsKey("passwordHash")
                    || !record.ContainsKey("iterations")
                    || !record.ContainsKey("createdAt"))
                {
                    return null;
                }
                return record;
            }
            catch
            {
                DurableFile.BackupCorruptFile(_authPath);
                return null;
            }
        }

        private void PersistLocalPassword(string password)
        {
            byte[] salt = RandomBytes(16);
            byte[] digest = Pbkdf2Sha256(password, salt, Pbkdf2Iterations, 32);
            var record = new Dictionary<string, object>
            {
                { "salt", Convert.ToBase64String(salt) },
                { "passwordHash", Convert.ToBase64String(digest) },
                { "iterations", Pbkdf2Iterations },
                { "createdAt", UnixTimeSeconds() }
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_authPath));
            DurableFile.WriteAllTextAtomically(_authPath, _json.Serialize(record), Encoding.UTF8);
        }

        private bool VerifyPassword(string password)
        {
            if (!_authEnabled)
            {
                return true;
            }
            if (!string.IsNullOrEmpty(_configuredAdminPassword))
            {
                return ConstantTimeEquals(
                    Encoding.UTF8.GetBytes(password ?? ""),
                    Encoding.UTF8.GetBytes(_configuredAdminPassword));
            }

            Dictionary<string, object> record = ReadLocalAuthRecord();
            if (record == null)
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(Convert.ToString(record["salt"]) ?? "");
                byte[] expected = Convert.FromBase64String(Convert.ToString(record["passwordHash"]) ?? "");
                int iterations = Convert.ToInt32(record["iterations"]);
                if (salt.Length == 0 || expected.Length == 0 || iterations <= 0)
                {
                    return false;
                }

                byte[] computed = Pbkdf2Sha256(password ?? "", salt, iterations, expected.Length);
                return ConstantTimeEquals(computed, expected);
            }
            catch
            {
                return false;
            }
        }

        private static string ValidatePassword(string password)
        {
            string value = password ?? "";
            if (value.Length < PasswordMinLength)
            {
                throw new InvalidOperationException("Le mot de passe doit contenir au moins " + PasswordMinLength + " caracteres.");
            }
            if (value.Length > PasswordMaxLength)
            {
                throw new InvalidOperationException("Le mot de passe ne peut pas depasser " + PasswordMaxLength + " caracteres.");
            }
            return value;
        }

        private static string GetBodyString(Dictionary<string, object> body, string key)
        {
            if (body == null || !body.ContainsKey(key) || body[key] == null)
            {
                return "";
            }
            return Convert.ToString(body[key]) ?? "";
        }

        private static bool EnvFlag(string name, bool defaultValue)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (raw == null)
            {
                return defaultValue;
            }

            raw = raw.Trim().ToLowerInvariant();
            return raw == "1" || raw == "true" || raw == "yes" || raw == "on";
        }

        private static int ReadIntEnv(string name, int defaultValue)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            int value;
            return int.TryParse(raw, out value) ? value : defaultValue;
        }

        private static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return bytes;
        }

        private static long UnixTimeSeconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static string GenerateToken(int byteCount)
        {
            string token = Convert.ToBase64String(RandomBytes(byteCount));
            return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] Pbkdf2Sha256(string password, byte[] salt, int iterations, int length)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(password ?? "")))
            {
                int hashLength = hmac.HashSize / 8;
                int blockCount = (int)Math.Ceiling((double)length / hashLength);
                byte[] output = new byte[length];
                int offset = 0;

                for (int blockIndex = 1; blockIndex <= blockCount; blockIndex++)
                {
                    byte[] block = Pbkdf2Block(hmac, salt, iterations, blockIndex);
                    int bytesToCopy = Math.Min(hashLength, length - offset);
                    Buffer.BlockCopy(block, 0, output, offset, bytesToCopy);
                    offset += bytesToCopy;
                }

                return output;
            }
        }

        private static byte[] Pbkdf2Block(HMACSHA256 hmac, byte[] salt, int iterations, int blockIndex)
        {
            byte[] input = new byte[salt.Length + 4];
            Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
            input[input.Length - 4] = (byte)(blockIndex >> 24);
            input[input.Length - 3] = (byte)(blockIndex >> 16);
            input[input.Length - 2] = (byte)(blockIndex >> 8);
            input[input.Length - 1] = (byte)blockIndex;

            byte[] u = hmac.ComputeHash(input);
            byte[] result = (byte[])u.Clone();
            for (int i = 1; i < iterations; i++)
            {
                u = hmac.ComputeHash(u);
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] ^= u[j];
                }
            }
            return result;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private static string[] SplitPath(string path)
        {
            return (path ?? "").Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string ExtractBoundary(string contentType)
        {
            const string key = "boundary=";
            int index = contentType.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return "";
            string value = contentType.Substring(index + key.Length).Trim();
            int semi = value.IndexOf(';');
            if (semi >= 0) value = value.Substring(0, semi).Trim();
            return value.Trim('"');
        }

        private static string MimeTypeFor(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".html") return "text/html; charset=utf-8";
            if (ext == ".css") return "text/css; charset=utf-8";
            if (ext == ".js" || ext == ".mjs") return "application/javascript; charset=utf-8";
            if (ext == ".json") return "application/json; charset=utf-8";
            if (ext == ".svg") return "image/svg+xml";
            if (ext == ".png") return "image/png";
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            if (ext == ".webp") return "image/webp";
            if (ext == ".ico") return "image/x-icon";
            if (ext == ".woff2") return "font/woff2";
            if (ext == ".woff") return "font/woff";
            if (ext == ".ttf") return "font/ttf";
            return "application/octet-stream";
        }

        private static string HtmlEscape(string value)
        {
            return (value ?? "")
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }
    }

    internal sealed class HttpRequest
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");
        private const long MaxJsonBodyBytes = 4L * 1024L * 1024L;
        private const long MaxUploadBodyBytes = 30L * 1024L * 1024L;
        private const long MaxWebSearchBodyBytes = 16L * 1024L;
        private const long MaxVoiceBodyBytes = 12L * 1024L * 1024L;

        public string Method;
        public string Target;
        public string Path;
        public string RemoteAddress;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string GetHeader(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public static HttpRequest Read(NetworkStream stream)
        {
            byte[] headerBytes = ReadHeaderBytes(stream);
            if (headerBytes == null || headerBytes.Length == 0)
            {
                return null;
            }

            string header = Latin1.GetString(headerBytes);
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var request = new HttpRequest();
            request.Method = requestLine[0].ToUpperInvariant();
            request.Target = requestLine[1];
            int queryIndex = request.Target.IndexOf('?');
            string rawPath = queryIndex >= 0 ? request.Target.Substring(0, queryIndex) : request.Target;
            request.Path = Uri.UnescapeDataString(rawPath);
            if (string.IsNullOrEmpty(request.Path)) request.Path = "/";

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            long contentLength = 0;
            string rawLength;
            if (request.Headers.TryGetValue("Content-Length", out rawLength))
            {
                if (!long.TryParse(rawLength, out contentLength) || contentLength < 0)
                {
                    throw new InvalidOperationException("Content-Length invalide.");
                }
            }

            if (contentLength > 0)
            {
                long maxBodyBytes = MaxBodyBytesFor(request);
                if (contentLength > maxBodyBytes || contentLength > int.MaxValue)
                {
                    throw new RequestBodyTooLargeException("Requete trop volumineuse.");
                }
                request.Body = ReadExact(stream, (int)contentLength);
            }

            return request;
        }

        private static long MaxBodyBytesFor(HttpRequest request)
        {
            string path = request == null ? "" : (request.Path ?? "");
            if (path.StartsWith("/api/conversations/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/attachments", StringComparison.OrdinalIgnoreCase))
            {
                return MaxUploadBodyBytes;
            }
            if (string.Equals(path, "/api/web-search", StringComparison.OrdinalIgnoreCase))
            {
                return MaxWebSearchBodyBytes;
            }
            if (string.Equals(path, "/api/voice/transcribe", StringComparison.OrdinalIgnoreCase))
            {
                return MaxVoiceBodyBytes;
            }
            return MaxJsonBodyBytes;
        }

        private static byte[] ReadHeaderBytes(NetworkStream stream)
        {
            var buffer = new List<byte>();
            int matched = 0;
            byte[] marker = new byte[] { 13, 10, 13, 10 };
            while (buffer.Count < 65536)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                byte b = (byte)value;
                buffer.Add(b);
                if (b == marker[matched])
                {
                    matched++;
                    if (matched == marker.Length) break;
                }
                else
                {
                    matched = b == marker[0] ? 1 : 0;
                }
            }
            return buffer.ToArray();
        }

        private static byte[] ReadExact(NetworkStream stream, int length)
        {
            byte[] body = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(body, offset, length - offset);
                if (read <= 0) break;
                offset += read;
            }
            if (offset == length) return body;
            byte[] partial = new byte[offset];
            Buffer.BlockCopy(body, 0, partial, 0, offset);
            return partial;
        }
    }

    internal sealed class HttpResponse
    {
        public readonly HttpStatusCode StatusCode;
        public readonly string ContentType;
        public readonly byte[] Body;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public HttpResponse(HttpStatusCode statusCode, string contentType, byte[] body)
        {
            StatusCode = statusCode;
            ContentType = contentType;
            Body = body ?? new byte[0];
            Headers["Connection"] = "close";
            Headers["X-Content-Type-Options"] = "nosniff";
            Headers["X-Frame-Options"] = "DENY";
            Headers["Referrer-Policy"] = "no-referrer";
            Headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
            Headers["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=(), payment=(), usb=()";
            Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        }

        public void Write(NetworkStream stream)
        {
            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append((int)StatusCode).Append(' ').Append(ReasonPhrase(StatusCode)).Append("\r\n");
            builder.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            builder.Append("Content-Length: ").Append(Body.Length).Append("\r\n");
            foreach (var pair in Headers)
            {
                builder.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            }
            builder.Append("\r\n");

            byte[] header = Encoding.ASCII.GetBytes(builder.ToString());
            stream.Write(header, 0, header.Length);
            if (Body.Length > 0)
            {
                stream.Write(Body, 0, Body.Length);
            }
        }

        private static string ReasonPhrase(HttpStatusCode status)
        {
            if (status == HttpStatusCode.OK) return "OK";
            if (status == HttpStatusCode.Created) return "Created";
            if (status == HttpStatusCode.BadRequest) return "Bad Request";
            if (status == HttpStatusCode.Unauthorized) return "Unauthorized";
            if (status == HttpStatusCode.Forbidden) return "Forbidden";
            if (status == HttpStatusCode.NotFound) return "Not Found";
            if (status == HttpStatusCode.MethodNotAllowed) return "Method Not Allowed";
            if ((int)status == 413) return "Payload Too Large";
            if ((int)status == 429) return "Too Many Requests";
            if (status == HttpStatusCode.Gone) return "Gone";
            if (status == HttpStatusCode.InternalServerError) return "Internal Server Error";
            return status.ToString();
        }
    }

    internal sealed class DataStore
    {
        private const int CurrentStoreSchemaVersion = 1;
        private const int MaxAttachmentCount = 5;
        private const int MaxAttachmentFileNameChars = 180;
        private const long MaxImageAttachmentBytes = 10L * 1024L * 1024L;
        private const long MaxPdfAttachmentBytes = 20L * 1024L * 1024L;
        private const long MaxTextAttachmentBytes = 2L * 1024L * 1024L;
        private const long MaxAttachmentTotalBytes = 25L * 1024L * 1024L;
        private const int MaxWebSourceCount = 5;
        private const int MaxWebSourceTitleChars = 180;
        private const int MaxWebSourceUrlChars = 500;
        private const int MaxWebSourceSnippetChars = 700;
        private const int MaxWebSourceSourceChars = 120;
        private readonly object _lock = new object();
        private readonly string _dataDir;
        private readonly string _storePath;
        private readonly string _uploadsDir;
        private readonly JavaScriptSerializer _json;
        private AppData _data;

        public DataStore(string dataDir)
        {
            _dataDir = dataDir;
            _storePath = Path.Combine(dataDir, "kivrio-chat.json");
            _uploadsDir = Path.Combine(dataDir, "uploads");
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_uploadsDir);
            _data = Load();
            RefreshRecoveryBackupFromActiveStore();
        }

        public Dictionary<string, object> GetSystemPrompt()
        {
            lock (_lock)
            {
                return new Dictionary<string, object>
                {
                    { "prompt", _data.systemPrompt ?? "" },
                    { "updatedAt", _data.systemPromptUpdatedAt }
                };
            }
        }

        public Dictionary<string, object> UpdateSystemPrompt(Dictionary<string, object> body)
        {
            lock (_lock)
            {
                _data.systemPrompt = GetString(body, "prompt", "");
                _data.systemPromptUpdatedAt = NowMs();
                Save();
                return GetSystemPrompt();
            }
        }

        public List<Dictionary<string, object>> ListFolders()
        {
            lock (_lock)
            {
                var output = new List<Dictionary<string, object>>();
                foreach (FolderRecord folder in _data.folders)
                {
                    output.Add(SerializeFolder(folder));
                }
                output.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
                {
                    return string.Compare(Convert.ToString(a["name"]), Convert.ToString(b["name"]), StringComparison.OrdinalIgnoreCase);
                });
                return output;
            }
        }

        public Dictionary<string, object> CreateFolder(Dictionary<string, object> body)
        {
            lock (_lock)
            {
                long now = NowMs();
                var folder = new FolderRecord
                {
                    id = NewId("f"),
                    name = CleanTitle(GetString(body, "name", "Nouveau dossier"), "Nouveau dossier", 80),
                    createdAt = now,
                    updatedAt = now
                };
                _data.folders.Add(folder);
                Save();
                return SerializeFolder(folder);
            }
        }

        public Dictionary<string, object> UpdateFolder(string id, Dictionary<string, object> body)
        {
            lock (_lock)
            {
                FolderRecord folder = FindFolder(id);
                if (folder == null) return null;
                if (body.ContainsKey("name"))
                {
                    folder.name = CleanTitle(GetString(body, "name", folder.name), folder.name, 80);
                }
                folder.updatedAt = NowMs();
                Save();
                return SerializeFolder(folder);
            }
        }

        public bool DeleteFolder(string id)
        {
            lock (_lock)
            {
                FolderRecord folder = FindFolder(id);
                if (folder == null) return false;
                _data.folders.Remove(folder);
                foreach (ConversationRecord conversation in _data.conversations)
                {
                    if (conversation.folderId == id) conversation.folderId = null;
                }
                SaveAfterUserDeletion();
                return true;
            }
        }

        public List<Dictionary<string, object>> ListConversations()
        {
            lock (_lock)
            {
                var output = new List<Dictionary<string, object>>();
                foreach (ConversationRecord conversation in _data.conversations)
                {
                    if (conversation.archived != 0) continue;
                    output.Add(SerializeConversation(conversation, false));
                }
                output.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
                {
                    long left = Convert.ToInt64(a["updatedAt"]);
                    long right = Convert.ToInt64(b["updatedAt"]);
                    return right.CompareTo(left);
                });
                return output;
            }
        }

        public Dictionary<string, object> CreateConversation(Dictionary<string, object> body)
        {
            lock (_lock)
            {
                long now = NowMs();
                var conversation = new ConversationRecord
                {
                    id = NewId("c"),
                    title = CleanTitle(GetString(body, "title", "Nouvelle conversation"), "Nouvelle conversation", 64),
                    folderId = GetString(body, "folder_id", GetString(body, "folderId", null)),
                    createdAt = now,
                    updatedAt = now,
                    archived = 0,
                    messages = new List<MessageRecord>()
                };
                _data.conversations.Add(conversation);
                Save();
                return SerializeConversation(conversation, true);
            }
        }

        public Dictionary<string, object> GetConversationPayload(string id)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(id);
                if (conversation == null) return null;
                return new Dictionary<string, object>
                {
                    { "conversation", SerializeConversation(conversation, false) },
                    { "messages", SerializeMessages(conversation) }
                };
            }
        }

        public List<Dictionary<string, object>> GetConversationMessages(string id)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(id);
                return conversation == null ? null : SerializeMessages(conversation);
            }
        }

        public Dictionary<string, object> UpdateConversation(string id, Dictionary<string, object> body)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(id);
                if (conversation == null) return null;
                if (body.ContainsKey("title")) conversation.title = CleanTitle(GetString(body, "title", conversation.title), conversation.title, 64);
                if (body.ContainsKey("folder_id")) conversation.folderId = GetNullableString(body, "folder_id");
                if (body.ContainsKey("folderId")) conversation.folderId = GetNullableString(body, "folderId");
                if (body.ContainsKey("archived")) conversation.archived = GetBool(body, "archived") ? 1 : 0;
                conversation.updatedAt = NowMs();
                Save();
                return SerializeConversation(conversation, false);
            }
        }

        public bool DeleteConversation(string id)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(id);
                if (conversation == null) return false;
                List<AttachmentRecord> removedAttachments = DetachAttachmentsForConversation(id);
                _data.conversations.Remove(conversation);
                SaveAfterUserDeletion();
                DeleteAttachmentFiles(removedAttachments);
                return true;
            }
        }

        public Dictionary<string, object> AddMessage(string conversationId, Dictionary<string, object> body)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(conversationId);
                if (conversation == null) return null;
                if (conversation.messages == null) conversation.messages = new List<MessageRecord>();

                long now = NowMs();
                var message = new MessageRecord
                {
                    id = NewId("m"),
                    conversationId = conversationId,
                    role = CleanRole(GetString(body, "role", "assistant")),
                    content = GetString(body, "content", ""),
                    reasoningText = GetNullableString(body, "reasoning_text") ?? GetNullableString(body, "reasoningText"),
                    model = GetNullableString(body, "model"),
                    reasoningDurationMs = GetNullableLong(body, "reasoning_duration_ms") ?? GetNullableLong(body, "reasoningDurationMs"),
                    webSources = GetWebSources(body),
                    createdAt = now,
                    position = conversation.messages.Count,
                    attachmentIds = GetStringList(body, "attachment_ids")
                };
                conversation.messages.Add(message);
                conversation.updatedAt = now;
                LinkAttachments(message);
                Save();
                return SerializeMessage(message);
            }
        }

        public Dictionary<string, object> UpdateMessage(string conversationId, string messageId, Dictionary<string, object> body)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(conversationId);
                if (conversation == null || conversation.messages == null) return null;
                int index = conversation.messages.FindIndex(delegate(MessageRecord item) { return item.id == messageId; });
                if (index < 0) return null;
                MessageRecord message = conversation.messages[index];
                if (body.ContainsKey("content")) message.content = GetString(body, "content", message.content);
                if (body.ContainsKey("role")) message.role = CleanRole(GetString(body, "role", message.role));
                if (body.ContainsKey("reasoning_text")) message.reasoningText = GetNullableString(body, "reasoning_text");
                if (body.ContainsKey("reasoningText")) message.reasoningText = GetNullableString(body, "reasoningText");
                if (body.ContainsKey("web_sources") || body.ContainsKey("webSources")) message.webSources = GetWebSources(body);
                List<AttachmentRecord> removedAttachmentsAfterSave = null;
                if (body.ContainsKey("truncate_following") && GetBool(body, "truncate_following"))
                {
                    int removeCount = conversation.messages.Count - index - 1;
                    if (removeCount > 0)
                    {
                        List<MessageRecord> removedMessages = conversation.messages.GetRange(index + 1, removeCount);
                        List<AttachmentRecord> removedAttachments = DetachAttachmentsForMessages(removedMessages);
                        conversation.messages.RemoveRange(index + 1, removeCount);
                        removedAttachmentsAfterSave = removedAttachments;
                    }
                }
                conversation.updatedAt = NowMs();
                Save();
                if (removedAttachmentsAfterSave != null)
                {
                    DeleteAttachmentFiles(removedAttachmentsAfterSave);
                }
                return new Dictionary<string, object>
                {
                    { "conversation", SerializeConversation(conversation, false) },
                    { "messages", SerializeMessages(conversation) }
                };
            }
        }

        public List<Dictionary<string, object>> CreateAttachments(string conversationId, List<UploadedFile> files)
        {
            lock (_lock)
            {
                ConversationRecord conversation = FindConversation(conversationId);
                if (conversation == null) return new List<Dictionary<string, object>>();
                if (files == null) files = new List<UploadedFile>();
                ValidateAttachments(files);

                var result = new List<Dictionary<string, object>>();
                var createdAttachments = new List<AttachmentRecord>();
                var createdPaths = new List<string>();
                try
                {
                    foreach (UploadedFile file in files)
                    {
                        if (file == null || file.Content == null) continue;
                        string id = NewId("a");
                        string safeName = SafeFileName(file.FileName);
                        string relativeDir = Path.Combine("uploads", conversationId, id);
                        string absoluteDir = Path.Combine(_dataDir, relativeDir);
                        Directory.CreateDirectory(absoluteDir);
                        string absolutePath = Path.Combine(absoluteDir, safeName);
                        createdPaths.Add(absolutePath);
                        DurableFile.WriteAllBytesAtomically(absolutePath, file.Content);

                        var attachment = new AttachmentRecord
                        {
                            id = id,
                            conversationId = conversationId,
                            messageId = null,
                            filename = safeName,
                            mimeType = StoredMimeTypeFor(safeName, file.ContentType),
                            sizeBytes = file.Content.LongLength,
                            relativePath = Path.Combine(relativeDir, safeName),
                            createdAt = NowMs()
                        };
                        _data.attachments.Add(attachment);
                        createdAttachments.Add(attachment);
                        result.Add(SerializeAttachment(attachment));
                    }
                    Save();
                    return result;
                }
                catch
                {
                    foreach (AttachmentRecord attachment in createdAttachments)
                    {
                        _data.attachments.Remove(attachment);
                    }
                    DeleteAttachmentPaths(createdPaths);
                    throw;
                }
            }
        }

        public AttachmentRecord GetAttachment(string id)
        {
            lock (_lock)
            {
                return _data.attachments.Find(delegate(AttachmentRecord item) { return item.id == id; });
            }
        }

        public string GetAttachmentPath(AttachmentRecord attachment)
        {
            if (attachment == null) return "";
            string fullPath = Path.GetFullPath(Path.Combine(_dataDir, attachment.relativePath ?? ""));
            if (!IsPathInsideDirectory(_uploadsDir, fullPath))
            {
                return "";
            }
            return fullPath;
        }

        private AppData Load()
        {
            AppData loaded;
            if (TryLoadStoreFile(_storePath, out loaded))
            {
                return NormalizeLoadedStore(loaded, true);
            }

            if (File.Exists(_storePath))
            {
                AppData backup;
                if (TryLoadStoreFile(_storePath + ".bak", out backup))
                {
                    DurableFile.BackupCorruptFile(_storePath);
                    AppData recovered = NormalizeLoadedStore(backup, false, false);
                    SaveData(recovered);
                    return recovered;
                }
                DurableFile.BackupCorruptFile(_storePath);
            }

            return Normalize(new AppData());
        }

        private bool TryLoadStoreFile(string path, out AppData data)
        {
            data = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                data = _json.Deserialize<AppData>(File.ReadAllText(path, Encoding.UTF8));
                return data != null;
            }
            catch
            {
                return false;
            }
        }

        private void Save()
        {
            Directory.CreateDirectory(_dataDir);
            DurableFile.WriteAllTextAtomically(_storePath, _json.Serialize(_data), Encoding.UTF8);
        }

        private void SaveData(AppData data)
        {
            Directory.CreateDirectory(_dataDir);
            DurableFile.WriteAllTextAtomically(_storePath, _json.Serialize(data), Encoding.UTF8);
        }

        private void SaveAfterUserDeletion()
        {
            string content = _json.Serialize(_data);
            Directory.CreateDirectory(_dataDir);
            DurableFile.WriteAllTextAtomically(_storePath, content, Encoding.UTF8);
            DurableFile.WriteAllTextAtomicallyWithoutBackup(_storePath + ".bak", content, Encoding.UTF8);
        }

        private void RefreshRecoveryBackupFromActiveStore()
        {
            if (!File.Exists(_storePath)) return;
            DurableFile.WriteAllTextAtomicallyWithoutBackup(_storePath + ".bak", _json.Serialize(_data), Encoding.UTF8);
        }

        private AppData NormalizeLoadedStore(AppData data, bool backupBeforeMigration)
        {
            return NormalizeLoadedStore(data, backupBeforeMigration, true);
        }

        private AppData NormalizeLoadedStore(AppData data, bool backupBeforeMigration, bool persistMigration)
        {
            int sourceVersion = data == null ? 0 : data.schemaVersion;
            bool needsMigration = sourceVersion < CurrentStoreSchemaVersion;
            AppData normalized = Normalize(data);
            if (needsMigration && persistMigration)
            {
                if (backupBeforeMigration)
                {
                    DurableFile.BackupBeforeMigration(_storePath, sourceVersion, CurrentStoreSchemaVersion);
                }
                SaveData(normalized);
            }
            return normalized;
        }

        private AppData Normalize(AppData data)
        {
            if (data == null) data = new AppData();
            if (data.schemaVersion < CurrentStoreSchemaVersion)
            {
                data.schemaVersion = CurrentStoreSchemaVersion;
            }
            if (data.folders == null) data.folders = new List<FolderRecord>();
            if (data.conversations == null) data.conversations = new List<ConversationRecord>();
            if (data.attachments == null) data.attachments = new List<AttachmentRecord>();
            foreach (ConversationRecord conversation in data.conversations)
            {
                if (conversation.messages == null) conversation.messages = new List<MessageRecord>();
                for (int i = 0; i < conversation.messages.Count; i++)
                {
                    MessageRecord message = conversation.messages[i];
                    if (message.attachmentIds == null) message.attachmentIds = new List<string>();
                    message.webSources = NormalizeWebSources(message.webSources);
                    message.position = i;
                }
            }
            return data;
        }

        private ConversationRecord FindConversation(string id)
        {
            return _data.conversations.Find(delegate(ConversationRecord item) { return item.id == id; });
        }

        private FolderRecord FindFolder(string id)
        {
            return _data.folders.Find(delegate(FolderRecord item) { return item.id == id; });
        }

        private void LinkAttachments(MessageRecord message)
        {
            if (message.attachmentIds == null) return;
            var validAttachmentIds = new List<string>();
            foreach (string attachmentId in message.attachmentIds)
            {
                AttachmentRecord attachment = _data.attachments.Find(delegate(AttachmentRecord item) { return item.id == attachmentId; });
                if (attachment != null && attachment.conversationId == message.conversationId)
                {
                    attachment.messageId = message.id;
                    validAttachmentIds.Add(attachmentId);
                }
            }
            message.attachmentIds = validAttachmentIds;
        }

        private List<AttachmentRecord> DetachAttachmentsForConversation(string conversationId)
        {
            var removed = new List<AttachmentRecord>();
            for (int i = _data.attachments.Count - 1; i >= 0; i--)
            {
                AttachmentRecord attachment = _data.attachments[i];
                if (attachment.conversationId == conversationId)
                {
                    removed.Add(attachment);
                    _data.attachments.RemoveAt(i);
                }
            }
            return removed;
        }

        private List<AttachmentRecord> DetachAttachmentsForMessages(List<MessageRecord> messages)
        {
            var messageIds = new HashSet<string>();
            foreach (MessageRecord message in messages ?? new List<MessageRecord>())
            {
                if (!string.IsNullOrEmpty(message.id))
                {
                    messageIds.Add(message.id);
                }
            }

            var removed = new List<AttachmentRecord>();
            if (messageIds.Count == 0) return removed;
            for (int i = _data.attachments.Count - 1; i >= 0; i--)
            {
                AttachmentRecord attachment = _data.attachments[i];
                if (messageIds.Contains(attachment.messageId))
                {
                    removed.Add(attachment);
                    _data.attachments.RemoveAt(i);
                }
            }
            return removed;
        }

        private void DeleteAttachmentFiles(List<AttachmentRecord> attachments)
        {
            foreach (AttachmentRecord attachment in attachments ?? new List<AttachmentRecord>())
            {
                DeleteAttachmentFile(attachment);
            }
        }

        private void DeleteAttachmentPaths(List<string> paths)
        {
            foreach (string path in paths ?? new List<string>())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string fullPath = Path.GetFullPath(path);
                    if (!IsPathInsideDirectory(_uploadsDir, fullPath)) continue;
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                    DeleteEmptyUploadDirectories(Path.GetDirectoryName(fullPath));
                }
                catch
                {
                }
            }
        }

        private void DeleteAttachmentFile(AttachmentRecord attachment)
        {
            try
            {
                string filePath = GetAttachmentPath(attachment);
                if (string.IsNullOrEmpty(filePath)) return;
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                DeleteEmptyUploadDirectories(Path.GetDirectoryName(filePath));
            }
            catch
            {
            }
        }

        private void DeleteEmptyUploadDirectories(string startDir)
        {
            if (string.IsNullOrEmpty(startDir)) return;
            string current = Path.GetFullPath(startDir);
            while (IsPathInsideDirectory(_uploadsDir, current))
            {
                if (!Directory.Exists(current)) break;
                if (Directory.GetFileSystemEntries(current).Length > 0) break;
                Directory.Delete(current);
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current)) break;
            }
        }

        private Dictionary<string, object> SerializeFolder(FolderRecord folder)
        {
            return new Dictionary<string, object>
            {
                { "id", folder.id },
                { "name", folder.name },
                { "createdAt", folder.createdAt },
                { "updatedAt", folder.updatedAt },
                { "conversationCount", CountConversationsInFolder(folder.id) }
            };
        }

        private Dictionary<string, object> SerializeConversation(ConversationRecord conversation, bool includeMessages)
        {
            var result = new Dictionary<string, object>
            {
                { "id", conversation.id },
                { "title", conversation.title },
                { "folderId", conversation.folderId },
                { "createdAt", conversation.createdAt },
                { "updatedAt", conversation.updatedAt },
                { "archived", conversation.archived },
                { "messageCount", conversation.messages == null ? 0 : conversation.messages.Count }
            };
            if (includeMessages)
            {
                result["messages"] = SerializeMessages(conversation);
            }
            return result;
        }

        private List<Dictionary<string, object>> SerializeMessages(ConversationRecord conversation)
        {
            var output = new List<Dictionary<string, object>>();
            if (conversation.messages == null) return output;
            foreach (MessageRecord message in conversation.messages)
            {
                output.Add(SerializeMessage(message));
            }
            return output;
        }

        private Dictionary<string, object> SerializeMessage(MessageRecord message)
        {
            return new Dictionary<string, object>
            {
                { "id", message.id },
                { "conversationId", message.conversationId },
                { "role", message.role },
                { "content", message.content ?? "" },
                { "reasoningText", message.reasoningText },
                { "model", message.model },
                { "reasoningDurationMs", message.reasoningDurationMs },
                { "webSources", SerializeWebSources(message.webSources) },
                { "createdAt", message.createdAt },
                { "position", message.position },
                { "attachments", SerializeAttachmentsForMessage(message.id) }
            };
        }

        private static List<Dictionary<string, object>> SerializeWebSources(List<WebSourceRecord> sources)
        {
            var output = new List<Dictionary<string, object>>();
            foreach (WebSourceRecord source in NormalizeWebSources(sources))
            {
                output.Add(new Dictionary<string, object>
                {
                    { "index", source.index },
                    { "title", source.title },
                    { "url", source.url },
                    { "snippet", source.snippet },
                    { "source", source.source }
                });
            }
            return output;
        }

        private List<Dictionary<string, object>> SerializeAttachmentsForMessage(string messageId)
        {
            var output = new List<Dictionary<string, object>>();
            foreach (AttachmentRecord attachment in _data.attachments)
            {
                if (attachment.messageId == messageId)
                {
                    output.Add(SerializeAttachment(attachment));
                }
            }
            return output;
        }

        private Dictionary<string, object> SerializeAttachment(AttachmentRecord attachment)
        {
            bool isImage = (attachment.mimeType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            bool isPdf = string.Equals(NormalizeMimeType(attachment.mimeType), "application/pdf", StringComparison.OrdinalIgnoreCase);
            string contentUrl = "/api/attachments/" + Uri.EscapeDataString(attachment.id) + "/content";
            string textUrl = "/api/attachments/" + Uri.EscapeDataString(attachment.id) + "/text";
            return new Dictionary<string, object>
            {
                { "id", attachment.id },
                { "conversationId", attachment.conversationId },
                { "messageId", attachment.messageId },
                { "filename", attachment.filename },
                { "mimeType", attachment.mimeType },
                { "sizeBytes", attachment.sizeBytes },
                { "url", contentUrl },
                { "textUrl", isPdf ? textUrl : null },
                { "previewUrl", isImage ? contentUrl : null },
                { "isImage", isImage },
                { "isPdf", isPdf },
                { "status", "stored" }
            };
        }

        private int CountConversationsInFolder(string folderId)
        {
            int count = 0;
            foreach (ConversationRecord conversation in _data.conversations)
            {
                if (conversation.folderId == folderId && conversation.archived == 0) count++;
            }
            return count;
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private static string NewId(string prefix)
        {
            return prefix + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private static string CleanTitle(string value, string fallback, int max)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) text = fallback;
            if (text.Length > max) text = text.Substring(0, max);
            return text;
        }

        private static string CleanRole(string value)
        {
            string role = (value ?? "").Trim().ToLowerInvariant();
            if (role == "user" || role == "assistant" || role == "system") return role;
            return "assistant";
        }

        private static string SafeFileName(string value)
        {
            string name = Path.GetFileName(value ?? "fichier");
            if (string.IsNullOrWhiteSpace(name)) name = "fichier";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            var builder = new StringBuilder();
            foreach (char c in name.Trim())
            {
                builder.Append(c < 32 ? '_' : c);
            }

            name = builder.ToString().Trim();
            string extension = Path.GetExtension(name);
            string baseName = Path.GetFileNameWithoutExtension(name).Trim('.', ' ');
            if (string.IsNullOrWhiteSpace(baseName))
            {
                name = "fichier" + extension;
            }
            if (name.Length > MaxAttachmentFileNameChars)
            {
                extension = Path.GetExtension(name);
                int baseLimit = Math.Max(1, MaxAttachmentFileNameChars - extension.Length);
                baseName = Path.GetFileNameWithoutExtension(name);
                if (baseName.Length > baseLimit)
                {
                    baseName = baseName.Substring(0, baseLimit);
                }
                name = baseName + extension;
            }
            return name;
        }

        private static bool IsPathInsideDirectory(string rootDir, string candidatePath)
        {
            if (string.IsNullOrEmpty(rootDir) || string.IsNullOrEmpty(candidatePath)) return false;
            string root = Path.GetFullPath(rootDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(candidatePath);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateAttachments(List<UploadedFile> files)
        {
            int count = 0;
            long totalBytes = 0;
            if (files == null) return;

            foreach (UploadedFile file in files)
            {
                if (file == null || file.Content == null) continue;
                count++;
                if (count > MaxAttachmentCount)
                {
                    throw new UploadValidationException((HttpStatusCode)413, "Maximum " + MaxAttachmentCount + " fichiers par message.");
                }

                string safeName = SafeFileName(file.FileName);
                string kind = AttachmentKindFor(safeName, file.ContentType);
                if (string.IsNullOrEmpty(kind))
                {
                    throw new UploadValidationException(HttpStatusCode.BadRequest, "Type de fichier non pris en charge: " + safeName);
                }

                long size = file.Content.LongLength;
                long limit = AttachmentSizeLimit(kind);
                if (size > limit)
                {
                    throw new UploadValidationException((HttpStatusCode)413, "Fichier trop volumineux: " + safeName);
                }

                ValidateAttachmentContent(safeName, kind, file.Content);

                totalBytes += size;
                if (totalBytes > MaxAttachmentTotalBytes)
                {
                    throw new UploadValidationException((HttpStatusCode)413, "Le total des fichiers depasse la limite autorisee.");
                }
            }
        }

        private static long AttachmentSizeLimit(string kind)
        {
            if (kind == "image") return MaxImageAttachmentBytes;
            if (kind == "pdf") return MaxPdfAttachmentBytes;
            if (kind == "text") return MaxTextAttachmentBytes;
            return 0;
        }

        private static void ValidateAttachmentContent(string safeName, string kind, byte[] content)
        {
            if (kind == "image" && !IsExpectedImageContent(safeName, content))
            {
                throw new UploadValidationException(HttpStatusCode.BadRequest, "Contenu du fichier invalide: " + safeName);
            }
            if (kind == "pdf" && !LooksLikePdf(content))
            {
                throw new UploadValidationException(HttpStatusCode.BadRequest, "Contenu du fichier invalide: " + safeName);
            }
            if (kind == "text" && !LooksLikeSafeText(content))
            {
                throw new UploadValidationException(HttpStatusCode.BadRequest, "Contenu du fichier invalide: " + safeName);
            }
        }

        private static bool IsExpectedImageContent(string fileName, byte[] content)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg") return StartsWithBytes(content, new byte[] { 0xFF, 0xD8, 0xFF });
            if (ext == ".png") return StartsWithBytes(content, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            if (ext == ".webp") return StartsWithAscii(content, 0, "RIFF") && StartsWithAscii(content, 8, "WEBP");
            return false;
        }

        private static bool LooksLikePdf(byte[] content)
        {
            int offset = FirstMeaningfulByteOffset(content, 1024);
            return StartsWithAscii(content, offset, "%PDF-");
        }

        private static int FirstMeaningfulByteOffset(byte[] content, int maxScan)
        {
            if (content == null || content.Length == 0) return 0;
            int offset = StartsWithBytes(content, new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            int limit = Math.Min(content.Length, Math.Max(0, maxScan));
            while (offset < limit)
            {
                byte b = content[offset];
                if (b != 0x09 && b != 0x0A && b != 0x0C && b != 0x0D && b != 0x20)
                {
                    break;
                }
                offset++;
            }
            return offset;
        }

        private static bool LooksLikeSafeText(byte[] content)
        {
            if (content == null) return false;
            if (HasBinarySignature(content)) return false;

            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(content);
            }
            catch
            {
                return false;
            }

            foreach (char ch in text)
            {
                if (ch < 32 && ch != '\t' && ch != '\r' && ch != '\n' && ch != '\f')
                {
                    return false;
                }
            }

            return !ContainsActiveHtml(text);
        }

        private static bool HasBinarySignature(byte[] content)
        {
            return StartsWithBytes(content, new byte[] { 0xFF, 0xD8, 0xFF })
                || StartsWithBytes(content, new byte[] { 0x89, 0x50, 0x4E, 0x47 })
                || StartsWithAscii(content, 0, "RIFF")
                || StartsWithAscii(content, 0, "%PDF-")
                || StartsWithAscii(content, 0, "MZ")
                || StartsWithAscii(content, 0, "PK\u0003\u0004")
                || StartsWithAscii(content, 0, "GIF87a")
                || StartsWithAscii(content, 0, "GIF89a");
        }

        private static bool ContainsActiveHtml(string text)
        {
            string lower = (text ?? "").ToLowerInvariant();
            return lower.Contains("<!doctype html")
                || lower.Contains("<html")
                || lower.Contains("<script")
                || lower.Contains("</script")
                || lower.Contains("<iframe")
                || lower.Contains("<object")
                || lower.Contains("<embed")
                || lower.Contains("<svg")
                || lower.Contains("javascript:");
        }

        private static bool StartsWithAscii(byte[] content, int offset, string value)
        {
            if (content == null || value == null || offset < 0 || content.Length < offset + value.Length)
            {
                return false;
            }
            for (int i = 0; i < value.Length; i++)
            {
                if (content[offset + i] != (byte)value[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static bool StartsWithBytes(byte[] content, byte[] prefix)
        {
            if (content == null || prefix == null || content.Length < prefix.Length)
            {
                return false;
            }
            for (int i = 0; i < prefix.Length; i++)
            {
                if (content[i] != prefix[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static string StoredMimeTypeFor(string fileName, string contentType)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            if (ext == ".png") return "image/png";
            if (ext == ".webp") return "image/webp";
            if (ext == ".pdf") return "application/pdf";
            if (ext == ".md") return "text/markdown";
            if (ext == ".txt") return "text/plain";

            string mime = NormalizeMimeType(contentType);
            return string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
        }

        private static string AttachmentKindFor(string fileName, string contentType)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            string mime = NormalizeMimeType(contentType);

            if (ext == ".jpg" || ext == ".jpeg")
            {
                return IsMimeAllowed(mime, "image/jpeg") ? "image" : null;
            }
            if (ext == ".png")
            {
                return IsMimeAllowed(mime, "image/png") ? "image" : null;
            }
            if (ext == ".webp")
            {
                return IsMimeAllowed(mime, "image/webp") ? "image" : null;
            }
            if (ext == ".pdf")
            {
                return IsMimeAllowed(mime, "application/pdf") ? "pdf" : null;
            }
            if (ext == ".txt")
            {
                return IsTextMimeAllowed(mime) ? "text" : null;
            }
            if (ext == ".md")
            {
                return IsMarkdownMimeAllowed(mime) ? "text" : null;
            }
            return null;
        }

        private static string NormalizeMimeType(string contentType)
        {
            string mime = (contentType ?? "").Trim().ToLowerInvariant();
            int semi = mime.IndexOf(';');
            if (semi >= 0) mime = mime.Substring(0, semi).Trim();
            return mime;
        }

        private static bool IsMimeAllowed(string actual, string expected)
        {
            return string.IsNullOrEmpty(actual)
                || actual == "application/octet-stream"
                || actual == expected;
        }

        private static bool IsTextMimeAllowed(string actual)
        {
            return IsLooseMime(actual)
                || actual == "text/plain";
        }

        private static bool IsMarkdownMimeAllowed(string actual)
        {
            return IsLooseMime(actual)
                || actual == "text/plain"
                || actual == "application/markdown"
                || actual == "text/markdown"
                || actual == "text/x-markdown";
        }

        private static bool IsLooseMime(string actual)
        {
            return string.IsNullOrEmpty(actual)
                || actual == "application/octet-stream";
        }

        private static string GetString(Dictionary<string, object> body, string key, string fallback)
        {
            object value;
            if (body != null && body.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToString(value);
            }
            return fallback;
        }

        private static string GetNullableString(Dictionary<string, object> body, string key)
        {
            object value;
            if (body != null && body.TryGetValue(key, out value) && value != null)
            {
                string text = Convert.ToString(value);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            return null;
        }

        private static bool GetBool(Dictionary<string, object> body, string key)
        {
            object value;
            if (body == null || !body.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            string text = Convert.ToString(value).Trim().ToLowerInvariant();
            return text == "1" || text == "true" || text == "yes" || text == "on";
        }

        private static long? GetNullableLong(Dictionary<string, object> body, string key)
        {
            object value;
            if (body == null || !body.TryGetValue(key, out value) || value == null) return null;
            long parsed;
            if (long.TryParse(Convert.ToString(value), out parsed)) return parsed;
            return null;
        }

        private static List<WebSourceRecord> GetWebSources(Dictionary<string, object> body)
        {
            object value;
            if (body == null) return new List<WebSourceRecord>();
            if (!body.TryGetValue("web_sources", out value) && !body.TryGetValue("webSources", out value))
            {
                return new List<WebSourceRecord>();
            }

            object[] array = value as object[];
            if (array == null) return new List<WebSourceRecord>();

            var output = new List<WebSourceRecord>();
            foreach (object item in array)
            {
                if (output.Count >= MaxWebSourceCount) break;
                var source = item as Dictionary<string, object>;
                if (source == null) continue;

                string url = NormalizeWebSourceUrl(GetString(source, "url", ""));
                if (url.Length == 0) continue;

                string title = CleanWebSourceText(GetString(source, "title", url), MaxWebSourceTitleChars);
                string snippet = CleanWebSourceText(GetString(source, "snippet", GetString(source, "content", "")), MaxWebSourceSnippetChars);
                string engine = CleanWebSourceText(GetString(source, "source", GetString(source, "engine", "")), MaxWebSourceSourceChars);

                output.Add(new WebSourceRecord
                {
                    index = output.Count + 1,
                    title = title.Length == 0 ? url : title,
                    url = url,
                    snippet = snippet,
                    source = engine
                });
            }
            return output;
        }

        private static List<WebSourceRecord> NormalizeWebSources(List<WebSourceRecord> sources)
        {
            var output = new List<WebSourceRecord>();
            if (sources == null) return output;

            foreach (WebSourceRecord source in sources)
            {
                if (output.Count >= MaxWebSourceCount) break;
                if (source == null) continue;

                string url = NormalizeWebSourceUrl(source.url);
                if (url.Length == 0) continue;

                string title = CleanWebSourceText(source.title, MaxWebSourceTitleChars);
                output.Add(new WebSourceRecord
                {
                    index = output.Count + 1,
                    title = title.Length == 0 ? url : title,
                    url = url,
                    snippet = CleanWebSourceText(source.snippet, MaxWebSourceSnippetChars),
                    source = CleanWebSourceText(source.source, MaxWebSourceSourceChars)
                });
            }
            return output;
        }

        private static string NormalizeWebSourceUrl(string raw)
        {
            Uri uri;
            if (!Uri.TryCreate((raw ?? "").Trim(), UriKind.Absolute, out uri)) return "";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
            return LimitWebSourceText(uri.ToString(), MaxWebSourceUrlChars);
        }

        private static string CleanWebSourceText(string raw, int maxLength)
        {
            string decoded = WebUtility.HtmlDecode(raw ?? "");
            var builder = new StringBuilder();
            bool insideTag = false;
            foreach (char ch in decoded)
            {
                if (ch == '<')
                {
                    insideTag = true;
                    continue;
                }
                if (ch == '>')
                {
                    insideTag = false;
                    continue;
                }
                if (!insideTag)
                {
                    builder.Append(ch);
                }
            }
            return LimitWebSourceText(CollapseWebSourceWhitespace(builder.ToString()), maxLength);
        }

        private static string CollapseWebSourceWhitespace(string value)
        {
            var builder = new StringBuilder();
            bool previousWasWhitespace = false;
            foreach (char ch in value ?? "")
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!previousWasWhitespace)
                    {
                        builder.Append(' ');
                        previousWasWhitespace = true;
                    }
                    continue;
                }
                builder.Append(ch);
                previousWasWhitespace = false;
            }
            return builder.ToString().Trim();
        }

        private static string LimitWebSourceText(string value, int maxLength)
        {
            string text = (value ?? "").Trim();
            if (maxLength <= 0 || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength).Trim();
        }

        private static List<string> GetStringList(Dictionary<string, object> body, string key)
        {
            var output = new List<string>();
            object value;
            if (body == null || !body.TryGetValue(key, out value) || value == null) return output;
            object[] array = value as object[];
            if (array != null)
            {
                foreach (object item in array)
                {
                    if (item != null) output.Add(Convert.ToString(item));
                }
            }
            return output;
        }
    }

    public sealed class AppData
    {
        public int schemaVersion { get; set; }
        public string systemPrompt { get; set; }
        public long systemPromptUpdatedAt { get; set; }
        public List<FolderRecord> folders { get; set; }
        public List<ConversationRecord> conversations { get; set; }
        public List<AttachmentRecord> attachments { get; set; }
    }

    public sealed class FolderRecord
    {
        public string id { get; set; }
        public string name { get; set; }
        public long createdAt { get; set; }
        public long updatedAt { get; set; }
    }

    public sealed class ConversationRecord
    {
        public string id { get; set; }
        public string title { get; set; }
        public string folderId { get; set; }
        public long createdAt { get; set; }
        public long updatedAt { get; set; }
        public int archived { get; set; }
        public List<MessageRecord> messages { get; set; }
    }

    public sealed class MessageRecord
    {
        public string id { get; set; }
        public string conversationId { get; set; }
        public string role { get; set; }
        public string content { get; set; }
        public string reasoningText { get; set; }
        public string model { get; set; }
        public long? reasoningDurationMs { get; set; }
        public List<WebSourceRecord> webSources { get; set; }
        public long createdAt { get; set; }
        public int position { get; set; }
        public List<string> attachmentIds { get; set; }
    }

    public sealed class WebSourceRecord
    {
        public int index { get; set; }
        public string title { get; set; }
        public string url { get; set; }
        public string snippet { get; set; }
        public string source { get; set; }
    }

    public sealed class AttachmentRecord
    {
        public string id { get; set; }
        public string conversationId { get; set; }
        public string messageId { get; set; }
        public string filename { get; set; }
        public string mimeType { get; set; }
        public long sizeBytes { get; set; }
        public string relativePath { get; set; }
        public long createdAt { get; set; }
    }

    internal sealed class UploadedFile
    {
        public string FileName;
        public string ContentType;
        public byte[] Content;
    }

    internal static class MultipartParser
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");

        public static bool IsSafeBoundary(string boundary)
        {
            if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 200)
            {
                return false;
            }

            foreach (char ch in boundary)
            {
                bool allowed = (ch >= 'a' && ch <= 'z')
                    || (ch >= 'A' && ch <= 'Z')
                    || (ch >= '0' && ch <= '9')
                    || ch == '\''
                    || ch == '('
                    || ch == ')'
                    || ch == '+'
                    || ch == '_'
                    || ch == ','
                    || ch == '-'
                    || ch == '.'
                    || ch == '/'
                    || ch == ':'
                    || ch == '='
                    || ch == '?';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        public static List<UploadedFile> Parse(byte[] body, string boundary)
        {
            var files = new List<UploadedFile>();
            byte[] bytes = body ?? new byte[0];
            byte[] marker = Latin1.GetBytes("--" + boundary);
            byte[] headerSeparator = new byte[] { 13, 10, 13, 10 };
            int markerIndex = FindNextMarker(bytes, marker, 0);

            while (markerIndex >= 0)
            {
                int start = markerIndex + marker.Length;
                if (StartsWith(bytes, start, new byte[] { 45, 45 })) break;
                if (StartsWith(bytes, start, new byte[] { 13, 10 }))
                {
                    start += 2;
                }
                else
                {
                    break;
                }

                int headerEnd = IndexOf(bytes, headerSeparator, start);
                if (headerEnd < 0) break;
                int contentStart = headerEnd + 4;
                int nextMarker = FindNextMarker(bytes, marker, contentStart);
                if (nextMarker < 0) break;
                int contentEnd = nextMarker;
                if (contentEnd >= 2 && bytes[contentEnd - 2] == 13 && bytes[contentEnd - 1] == 10) contentEnd -= 2;

                string headerText = Latin1.GetString(bytes, start, headerEnd - start);
                byte[] content = CopyRange(bytes, contentStart, Math.Max(0, contentEnd - contentStart));
                var headers = ParseHeaders(headerText);
                string disposition;
                if (headers.TryGetValue("Content-Disposition", out disposition))
                {
                    string fileName = HeaderParameter(disposition, "filename");
                    if (string.IsNullOrEmpty(fileName))
                    {
                        fileName = HeaderParameter(disposition, "filename*");
                    }
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        string contentType;
                        headers.TryGetValue("Content-Type", out contentType);
                        files.Add(new UploadedFile
                        {
                            FileName = fileName,
                            ContentType = contentType ?? "application/octet-stream",
                            Content = content
                        });
                    }
                }
                markerIndex = nextMarker;
            }

            return files;
        }

        private static int FindNextMarker(byte[] body, byte[] marker, int start)
        {
            int index = Math.Max(0, start);
            while (true)
            {
                int found = IndexOf(body, marker, index);
                if (found < 0)
                {
                    return -1;
                }
                if (found == 0 || (found >= 2 && body[found - 2] == 13 && body[found - 1] == 10))
                {
                    return found;
                }
                index = found + 1;
            }
        }

        private static int IndexOf(byte[] body, byte[] pattern, int start)
        {
            if (body == null || pattern == null || pattern.Length == 0 || body.Length < pattern.Length)
            {
                return -1;
            }

            int limit = body.Length - pattern.Length;
            for (int i = Math.Max(0, start); i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (body[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool StartsWith(byte[] body, int offset, byte[] pattern)
        {
            if (body == null || pattern == null || offset < 0 || body.Length < offset + pattern.Length)
            {
                return false;
            }
            for (int i = 0; i < pattern.Length; i++)
            {
                if (body[offset + i] != pattern[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static byte[] CopyRange(byte[] body, int offset, int length)
        {
            if (body == null || length <= 0)
            {
                return new byte[0];
            }
            byte[] copy = new byte[length];
            Buffer.BlockCopy(body, offset, copy, 0, length);
            return copy;
        }

        private static Dictionary<string, string> ParseHeaders(string text)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            return headers;
        }

        private static string HeaderParameter(string header, string name)
        {
            string[] parts = (header ?? "").Split(';');
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;
                string key = part.Substring(0, equals).Trim();
                if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                return DecodeHeaderParameterValue(part.Substring(equals + 1).Trim());
            }
            return "";
        }

        private static string DecodeHeaderParameterValue(string value)
        {
            string text = value ?? "";
            if (text.StartsWith("\"", StringComparison.Ordinal) && text.EndsWith("\"", StringComparison.Ordinal) && text.Length >= 2)
            {
                text = UnescapeQuotedString(text.Substring(1, text.Length - 2));
            }
            int encodingSeparator = text.IndexOf("''", StringComparison.Ordinal);
            if (encodingSeparator > 0 && encodingSeparator + 2 < text.Length)
            {
                string charset = text.Substring(0, encodingSeparator);
                string encoded = text.Substring(encodingSeparator + 2);
                if (charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                {
                    text = Uri.UnescapeDataString(encoded);
                }
            }
            if (text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0 || text.IndexOf('\0') >= 0)
            {
                return "";
            }
            return text;
        }

        private static string UnescapeQuotedString(string value)
        {
            var builder = new StringBuilder();
            bool escaped = false;
            foreach (char ch in value ?? "")
            {
                if (escaped)
                {
                    builder.Append(ch);
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                builder.Append(ch);
            }
            if (escaped)
            {
                builder.Append('\\');
            }
            return builder.ToString();
        }
    }
}
