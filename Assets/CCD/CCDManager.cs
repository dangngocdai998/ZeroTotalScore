using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public sealed class CCDManager : MonoBehaviour
{
    public static CCDManager Instance { get; private set; }

    [Header("Unity CCD")]
    [SerializeField] private string baseUrl = "https://services.api.unity.com/ccd/v1";
    [SerializeField] private string projectId;
    [SerializeField] private string bucketId;
    [SerializeField] private string environmentId;
    [SerializeField] private string accessToken;
    [SerializeField, Min(1)] private int maxRetries = 3;
    [SerializeField, Min(1)] private int timeoutSeconds = 60;
    [SerializeField] private bool persistCache = true;

    private readonly Dictionary<string, byte[]> memoryCache = new Dictionary<string, byte[]>();
    private string cachePath;
    private string activeReleaseId;

    public event Action<float> DownloadProgress;
    public event Action<string> DownloadStarted;
    public event Action<string, byte[]> DownloadCompleted;
    public event Action<string, string> RequestFailed;
    public event Action<CCDUpdateInfo> UpdateChecked;

    public string BaseUrl => baseUrl;
    public string ProjectId => projectId;
    public string BucketId => bucketId;
    public string EnvironmentId => environmentId;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(baseUrl) &&
                                !string.IsNullOrWhiteSpace(projectId) &&
                                !string.IsNullOrWhiteSpace(bucketId);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        cachePath = Path.Combine(Application.persistentDataPath, "ccd-cache");
        if (persistCache)
            Directory.CreateDirectory(cachePath);
        activeReleaseId = PlayerPrefs.GetString(ReleasePreferenceKey(), string.Empty);
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public void Configure(string newProjectId, string newBucketId, string newEnvironmentId, string newAccessToken)
    {
        projectId = newProjectId;
        bucketId = newBucketId;
        environmentId = newEnvironmentId;
        accessToken = newAccessToken;
    }

    public Coroutine ListReleases(Action<CCDReleaseList> onSuccess, Action<string> onError = null)
    {
        return StartCoroutine(SendJsonRequest<CCDReleaseList>("GET", ReleasesPath(), null, onSuccess, onError));
    }

    public Coroutine CheckForUpdate(Action<CCDUpdateInfo> onSuccess, Action<string> onError = null)
    {
        return ListReleases(releaseList =>
        {
            CCDRelease latest = releaseList != null ? releaseList.Latest() : null;
            if (latest == null)
            {
                NotifyFailure("releases", "CCD returned no releases.", onError);
                return;
            }

            CCDUpdateInfo result = new CCDUpdateInfo
            {
                latestRelease = latest,
                currentReleaseId = activeReleaseId,
                hasUpdate = !string.Equals(activeReleaseId, latest.Id(), StringComparison.OrdinalIgnoreCase)
            };
            UpdateChecked?.Invoke(result);
            onSuccess?.Invoke(result);
        }, onError);
    }

    public void UseRelease(string releaseId)
    {
        activeReleaseId = releaseId ?? string.Empty;
        PlayerPrefs.SetString(ReleasePreferenceKey(), activeReleaseId);
        PlayerPrefs.Save();
    }

    public Coroutine GetRelease(string releaseId, Action<CCDRelease> onSuccess, Action<string> onError = null)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
            return Fail("Release id is required.", onError);

        return StartCoroutine(SendJsonRequest<CCDRelease>("GET", ReleasesPath() + "/" + Escape(releaseId), null, onSuccess, onError));
    }

    public Coroutine Download(string path, Action<byte[]> onSuccess, Action<string> onError = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail("A CCD content path is required.", onError);

        return StartCoroutine(DownloadRoutine(path, onSuccess, onError));
    }

    public Coroutine DownloadText(string path, Action<string> onSuccess, Action<string> onError = null)
    {
        return Download(path, bytes => onSuccess?.Invoke(Encoding.UTF8.GetString(bytes)), onError);
    }

    public Coroutine DownloadJson<T>(string path, Action<T> onSuccess, Action<string> onError = null)
    {
        return DownloadText(path, json =>
        {
            try
            {
                onSuccess?.Invoke(JsonUtility.FromJson<T>(json));
            }
            catch (Exception exception)
            {
                NotifyFailure(path, exception.Message, onError);
            }
        }, onError);
    }

    public void ClearCache()
    {
        memoryCache.Clear();
        if (Directory.Exists(cachePath))
            Directory.Delete(cachePath, true);
        if (persistCache)
            Directory.CreateDirectory(cachePath);
        UseRelease(string.Empty);
    }

    public void ResetCache()
    {
        ClearCache();
    }

    public void RemoveFromCache(string path)
    {
        string key = CacheKey(path);
        memoryCache.Remove(key);
        string file = CacheFile(key);
        if (File.Exists(file))
            File.Delete(file);
    }

    public void Remove(string path)
    {
        RemoveFromCache(path);
    }

    private IEnumerator DownloadRoutine(string path, Action<byte[]> onSuccess, Action<string> onError)
    {
        string key = CacheKey(path);
        DownloadStarted?.Invoke(key);

        byte[] cachedBytes;
        if (memoryCache.TryGetValue(key, out cachedBytes))
        {
            DownloadProgress?.Invoke(1f);
            DownloadCompleted?.Invoke(key, cachedBytes);
            onSuccess?.Invoke(cachedBytes);
            yield break;
        }

        string cachedFile = CacheFile(key);
        if (persistCache && File.Exists(cachedFile))
        {
            cachedBytes = File.ReadAllBytes(cachedFile);
            memoryCache[key] = cachedBytes;
            DownloadProgress?.Invoke(1f);
            DownloadCompleted?.Invoke(key, cachedBytes);
            onSuccess?.Invoke(cachedBytes);
            yield break;
        }

        string url = ContentUrl(key);
        string error = null;
        for (int attempt = 0; attempt <= Mathf.Max(0, maxRetries); attempt++)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = timeoutSeconds;
                AddAuthentication(request);
                request.downloadHandler = new DownloadHandlerBuffer();
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    DownloadProgress?.Invoke(request.downloadProgress);
                    yield return null;
                }

                if (request.result == UnityWebRequest.Result.Success)
                {
                    byte[] bytes = request.downloadHandler.data;
                    memoryCache[key] = bytes;
                    if (persistCache)
                    {
                        Directory.CreateDirectory(cachePath);
                        File.WriteAllBytes(cachedFile, bytes);
                    }
                    DownloadProgress?.Invoke(1f);
                    DownloadCompleted?.Invoke(key, bytes);
                    onSuccess?.Invoke(bytes);
                    yield break;
                }

                error = FormatError(request);
            }
            if (attempt < maxRetries)
                yield return new WaitForSecondsRealtime(Mathf.Min(8f, 1f + attempt));
        }

        NotifyFailure(key, error ?? "CCD download failed.", onError);
    }

    private IEnumerator SendJsonRequest<T>(string method, string path, string body, Action<T> onSuccess, Action<string> onError)
    {
        string error = null;
        for (int attempt = 0; attempt <= Mathf.Max(0, maxRetries); attempt++)
        {
            using (UnityWebRequest request = new UnityWebRequest(Url(path), method))
            {
                request.timeout = timeoutSeconds;
                AddAuthentication(request);
                if (!string.IsNullOrEmpty(body))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    request.uploadHandler = new UploadHandlerRaw(payload);
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.downloadHandler = new DownloadHandlerBuffer();
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        onSuccess?.Invoke(JsonUtility.FromJson<T>(request.downloadHandler.text));
                    }
                    catch (Exception exception)
                    {
                        NotifyFailure(path, exception.Message, onError);
                    }
                    yield break;
                }
                error = FormatError(request);
            }
            if (attempt < maxRetries)
                yield return new WaitForSecondsRealtime(Mathf.Min(8f, 1f + attempt));
        }
        NotifyFailure(path, error ?? "CCD request failed.", onError);
    }

    private string ReleasesPath()
    {
        string path = "/projects/" + Escape(projectId) + "/buckets/" + Escape(bucketId) + "/releases";
        if (!string.IsNullOrWhiteSpace(environmentId))
            path += "?environmentId=" + Uri.EscapeDataString(environmentId);
        return path;
    }

    private string ContentUrl(string path)
    {
        string releasePath = string.IsNullOrWhiteSpace(environmentId) ? "/content" : "/content/" + Escape(environmentId);
        return Url(releasePath + "/" + path);
    }

    private string Url(string path)
    {
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    private void AddAuthentication(UnityWebRequest request)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.SetRequestHeader("Authorization", accessToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? accessToken : "Bearer " + accessToken);
    }

    private Coroutine Fail(string message, Action<string> onError)
    {
        NotifyFailure(string.Empty, message, onError);
        return null;
    }

    private void NotifyFailure(string path, string message, Action<string> onError)
    {
        RequestFailed?.Invoke(path, message);
        onError?.Invoke(message);
    }

    private string FormatError(UnityWebRequest request)
    {
        return string.IsNullOrWhiteSpace(request.error) ? "HTTP " + request.responseCode : request.error;
    }

    private string NormalizePath(string path)
    {
        return path.Trim().Trim('/').Replace('\\', '/');
    }

    private string CacheFile(string key)
    {
        return Path.Combine(cachePath, Hash(key) + ".bin");
    }

    private string CacheKey(string path)
    {
        return activeReleaseId + "|" + NormalizePath(path);
    }

    private string ReleasePreferenceKey()
    {
        return "CCD.activeRelease." + projectId + "." + bucketId + "." + environmentId;
    }

    private static string Hash(string value)
    {
        unchecked
        {
            int hash = 23;
            for (int index = 0; index < value.Length; index++)
                hash = hash * 31 + value[index];
            return hash.ToString("X8");
        }
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value ?? string.Empty);
    }
}

[Serializable]
public class CCDReleaseList
{
    public CCDRelease[] entries;
    public CCDRelease[] releases;

    public CCDRelease Latest()
    {
        CCDRelease[] values = entries != null && entries.Length > 0 ? entries : releases;
        if (values == null || values.Length == 0)
            return null;

        CCDRelease latest = values[0];
        DateTime latestDate;
        if (!DateTime.TryParse(latest.created, out latestDate))
            return latest;

        for (int index = 1; index < values.Length; index++)
        {
            DateTime candidateDate;
            if (DateTime.TryParse(values[index].created, out candidateDate) && candidateDate > latestDate)
            {
                latest = values[index];
                latestDate = candidateDate;
            }
        }
        return latest;
    }
}

[Serializable]
public class CCDRelease
{
    public string releaseid;
    public string releaseId;
    public string created;
    public string notes;
    public string hash;
    public string version;

    public string Id()
    {
        return string.IsNullOrWhiteSpace(releaseId) ? releaseid : releaseId;
    }
}

[Serializable]
public class CCDUpdateInfo
{
    public bool hasUpdate;
    public string currentReleaseId;
    public CCDRelease latestRelease;
}
