using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace OSY
{
    [Serializable]
    public class ManageableQueue<T>
    {
        public Queue<T> queue { get; private set; }
        public bool isLock;
        public ManageableQueue(bool isLock = false)
        {
            queue = new Queue<T>();
            this.isLock = isLock;
        }
    }
    public struct FrameInfo
    {
        public object data;
        public string path;
        public FrameInfo(object framedata, string path)
        {
            this.data = framedata;
            this.path = path;
        }
    }
    [Serializable]
    public struct RandomRange
    {
        public float minValue;
        public float maxValue;
        public RandomRange(float minValue, float maxValue)
        {
            this.minValue = minValue;
            this.maxValue = maxValue;
        }
        public float GetValue() => minValue >= maxValue ? minValue : OSYUtils.GetRandom(minValue, maxValue);
    }
    public class Singleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;

        public static T Instance
        {
            get
            {
                if (_instance == null && !_instance.IsDestroyed())
                {
                    var instances = FindObjectsOfType(typeof(T));
                    if (instances == null || instances.Length == 0) return null;
                    _instance = (T)instances[0];
                    if (instances.Length > 0)
                        for (int i = 1; i < instances.Length; i++)
                        {
                            Destroy(instances[i]);
                        }
                }
                return _instance;
            }
        }
        protected virtual void Awake()
        {
            var tokenCreate = destroyCancellationToken;
            var instanceInit = Instance;
        }
    }
    public class OSYUtils
    {
        [HideInInspector]
        public static string EmptyString = "";
        public static async UniTask CachingTextureTask(string url)
        {
            if (url == null || url == OSYUtils.EmptyString || url == "\r\n")
                return;
            if (GameManager.Instance.thumbnailsCacheDic.ContainsKey(url))
                return;
            string originUrl = url;
            string protocol = url.Substring(0, 4);
            if (!protocol.Equals("http") && !protocol.Equals("blob"))
                url = $"file:///{url}";
            //url = url.Replace("http://", "https://");
            //Debug.LogWarning(url);
            using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url))
            {
                try
                {
                    await request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.ConnectionError)
                    {
                        //Debug.Log(request.error);
                        return;
                    }
                }
                catch (Exception)
                {
                    //Debug.LogWarning(e);
                    return;
                }
                GameManager.Instance.thumbnailsCacheDic.TryAdd(originUrl, ((DownloadHandlerTexture)request.downloadHandler).texture);
            }
        }

        public static async UniTask WaitUntilStopwatch(Func<bool> waitCondition, Stopwatch stopwatch, NativeArray<float> lastRecordTIme, string taskName, CancellationToken token, bool isDebug = false)
        {
            if (isDebug)
            {
                Debug.Log($"{taskName} 작업 시간 측정 시작");
                if (!stopwatch.IsRunning)
                    stopwatch.Start();
            }
            await WaitUntil(waitCondition, YieldCaches.UniTaskYield, token);
            if (isDebug)
            {
                stopwatch.Stop();
                lock (stopwatch)
                    Debug.Log($"{taskName} 작업 소비 시간: " + (lastRecordTIme[0] = stopwatch.ElapsedMilliseconds - lastRecordTIme[0]) / 1000f);
            }
        }
        public static async UniTask<bool> WaitUntil(TimeSpan timeout, Func<bool> func, YieldAwaitable yield, bool ignoreTimeScale = false, CancellationToken token = default)
        {
            token = token == default ? GameManager.Instance.destroyCancellationToken : token;
            bool isTimeout = false;
            UniTask.RunOnThreadPool(async () =>
            {
                await UniTask.Delay(timeout, ignoreTimeScale);
                isTimeout = true;
            }).Forget();
            while (!func.Invoke() && !token.IsCancellationRequested && !isTimeout)
            {
                await yield;
            }
            return !isTimeout;
        }
        public static async UniTask WaitWhile(TimeSpan timeout, Func<bool> func, YieldAwaitable yield, bool ignoreTimeScale = false, CancellationToken token = default)
        {
            token = token == default ? GameManager.Instance.destroyCancellationToken : token;
            bool isTimeout = false;
            UniTask.RunOnThreadPool(async () =>
            {
                await UniTask.Delay(timeout, ignoreTimeScale);
                isTimeout = true;
            }).Forget();
            while (func.Invoke() && !token.IsCancellationRequested && !isTimeout)
            {
                await yield;
            }
        }
        public static async UniTask WaitUntil(Func<bool> func, YieldAwaitable yield, CancellationToken token = default)
        {
            token = token == default ? GameManager.Instance.destroyCancellationToken : token;
            while (!token.IsCancellationRequested && !func.Invoke())
            {
                await yield;
            }

        }
        public static async UniTask WaitWhile(Func<bool> func, YieldAwaitable yield, CancellationToken token = default)
        {
            token = token == default ? GameManager.Instance.destroyCancellationToken : token;
            while (func.Invoke() && !token.IsCancellationRequested)
            {
                await yield;
            }
        }
        public static async UniTask WaitUntil(Func<bool> func, Func<UniTask> waitDelay, CancellationToken token = default)
        {
            token = token == default ? GameManager.Instance.destroyCancellationToken : token;
            while (!func.Invoke() && !token.IsCancellationRequested)
            {
                await waitDelay();
            }
        }
        public static async UniTask WaitWhile(Func<bool> func, UniTask waitDelay, CancellationToken token = default)
        {
            token = token == default ? GameManager.Instance.destroyCancellationToken : token;
            while (func.Invoke() && !token.IsCancellationRequested)
            {
                await waitDelay;
            }
        }
        public static void KeepAlive(params object[] items) => GC.KeepAlive(items);

        public static float GetRandom(float minimum, float maximum)
        {
            System.Random random = new System.Random();
            return (float)(random.NextDouble() * (maximum - minimum) + minimum);
        }
        public static Vector2 GetRandomPosition(RectTransform rectTransform)
        {
            return rectTransform.anchoredPosition + new Vector2(GetRandom(-rectTransform.rect.width / 2, rectTransform.rect.width / 2), GetRandom(-rectTransform.rect.height / 2, rectTransform.rect.height / 2));
        }
        public static int GetRandom(int minimum, int maximum)
        {
            System.Random random = new System.Random();
            return random.Next(minimum, maximum);
        }
        public class YieldCaches
        {
            private static WaitForEndOfFrame waitForEndOfFrame;
            public static WaitForEndOfFrame WaitForEndOfFrame
            {
                get
                {
                    if (waitForEndOfFrame == null)
                        return waitForEndOfFrame = new WaitForEndOfFrame();
                    return waitForEndOfFrame;
                }
            }
            private static WaitForSeconds waitFor1sec;
            public static WaitForSeconds WaitFor1sec
            {
                get
                {
                    if (waitFor1sec == null)
                        return waitFor1sec = new WaitForSeconds(1);
                    return waitFor1sec;
                }
            }
            private static WaitForSecondsRealtime waitFor1secReal;
            public static WaitForSecondsRealtime WaitFor1secReal
            {
                get
                {
                    if (waitFor1secReal == null)
                        return waitFor1secReal = new WaitForSecondsRealtime(1);
                    return waitFor1secReal;
                }
            }
            private static WaitForSecondsRealtime waitFor100millisecReal;
            public static WaitForSecondsRealtime WaitFor100millisecReal
            {
                get
                {
                    if (waitFor100millisecReal == null)
                        return waitFor100millisecReal = new WaitForSecondsRealtime(0.1f);
                    return waitFor100millisecReal;
                }
            }
            public static YieldAwaitable UniTaskYield = UniTask.Yield();
        }

        /// <summary>
        /// url을 통해 오디오 데이터를 다운로드 & 메모리에 캐싱합니다.
        /// 마지막에 해당 오디오 데이터를 AudioClip 객체로 변환하여 반환합니다.
        /// </summary>
        public static async UniTask<AudioClip> GetAudioClip(string url, AudioType audioType)
        {
            if (url.Equals(null) || url.Equals(OSYUtils.EmptyString)) return null;
            if (!url.Substring(0, 4).Equals("http"))
                url = $"file://{url}";
            try
            {
                using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(url, audioType))
                {
                    await request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError || request.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        Debug.LogError("오디오 가져오기 오류: " + request.error);
                        return null;
                    }
                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    await WaitUntil(() => clip.loadState == AudioDataLoadState.Loaded, YieldCaches.UniTaskYield);
                    return clip;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("오디오 가져오기 오류: " + e.Message);
                return null;
            }
            /*using (HttpClient client = new HttpClient())
            {
                try
                {
                    // Download the WAV file
                    byte[] wavData = await client.GetByteArrayAsync(url);

                    return OpenWavParser.ByteArrayToAudioClip(wavData);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error downloading and saving WAV file: {ex.Message}");
                    return null;
                }
            }*/
            // 결과: Unable to open DLL! Dynamic linking is not supported in WebAssembly builds due to limitations to performance and code size. Please statically link in the needed libraries.

            /*using (WebClient client = new WebClient())
            {
                try
                {
                    // Download the WAV file
                    var wavData = await client.DownloadDataTaskAsync(url);
                    return OpenWavParser.ByteArrayToAudioClip(wavData);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error downloading and saving WAV file: {ex.Message}");
                    return null;
                }
            }*/
            //결과: DownloadDataTaskAsync에서 멈춤

            /*WebRequest request = WebRequest.Create(url);
            using (WebResponse response = request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            using (MemoryStream memoryStream = new MemoryStream())
            {
                // Download the WAV file into a memory stream
                responseStream.CopyTo(memoryStream);

                // Convert memory stream to byte array
                byte[] wavData = memoryStream.ToArray();
                return OpenWavParser.ByteArrayToAudioClip(wavData);
            }*/
            //결과: 또 멈춤

            /*using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(url, audioType))
            {
                await request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    Debug.LogError(request.error);
                    return null;
                }
                return OpenWavParser.ByteArrayToAudioClip(request.downloadHandler.data);
            }*/
            // 결과: Uncaught TypeError: Failed to execute 'copyToChannel' on 'AudioBuffer': The provided Float32Array value must not be shared.
        }

        /// <summary>
        /// url을 통해 비디오 데이터를 다운로드 & saveDir 폴더에 setFileName으로 저장합니다.
        /// 마지막에 해당 비디오 파일의 이름과 확장자를 포함한, 최종 경로를 반환합니다.
        /// </summary>
        public static async UniTask<string> GetVideoFilePath(string url, string saveDir, string setFileName)
        {
            UnityWebRequest www = UnityWebRequest.Get(url);
            await www.SendWebRequest();
            string result = null;
            if (www.result == UnityWebRequest.Result.ConnectionError)
            {
                Debug.Log(www.error);
            }
            else if (www.result == UnityWebRequest.Result.Success)
            {
                byte[] bytes = www.downloadHandler.data;
                if (!Directory.Exists(saveDir))
                {
                    Directory.CreateDirectory(saveDir);
                }
                result = $"{saveDir}/{setFileName}";
                Stream stream = null;
                try
                {
                    stream = new FileStream(result, FileMode.Create);
                    stream.Write(bytes);
                }
                catch (Exception exp)
                {
                    Debug.LogException(exp);
                }
                finally
                {
                    stream?.Close();
                }
            }
            return result;
        }
        public static async UniTask<JObject> GetJObject(string url, CancellationToken token, bool isKeepTry = true)
        {
            if (!url.Substring(0, 4).Equals("http"))
                url = $"file://{url}";

            UnityWebRequest request = UnityWebRequest.Get(url);
            while (true)
            {
                try
                {
                    await request.SendWebRequest();
                    break;
                }
                catch (Exception)
                {
                    //Debug.LogWarning(e.Text);
                    if (!isKeepTry)
                        return null;
                    await UniTask.Delay(TimeSpan.FromSeconds(1f), false, PlayerLoopTiming.Update, token);
                    request = UnityWebRequest.Get(url);
                }
            }
            var result = request.downloadHandler.text;
            //Debug.LogWarning(result);
            return new JObject(JObject.Parse(result));
        }
        public static string ConvertToUtf8(string value)
        {
            // Get UTF16 bytes and convert UTF16 bytes to UTF8 bytes
            byte[] utf16Bytes = Encoding.Unicode.GetBytes(value);
            byte[] utf8Bytes = Encoding.Convert(Encoding.Unicode, Encoding.UTF8, utf16Bytes);

            // Return UTF8 bytes as ANSI string
            return Encoding.Default.GetString(utf8Bytes);
        }

        public static string EncryptString(string plainText, DateTime key)
        {
            using (Aes aesAlg = Aes.Create())
            {
                Rfc2898DeriveBytes keyDerivation = new Rfc2898DeriveBytes(key.ToString("yyyy-MM-dd HH:mm:ss"), Encoding.UTF8.GetBytes("SaltValue"));
                aesAlg.Key = keyDerivation.GetBytes(aesAlg.KeySize / 8);
                aesAlg.IV = new byte[16]; // Initialization Vector (IV) - You may want to generate a random IV for added security

                ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                        {
                            swEncrypt.Write(plainText);
                        }
                    }

                    byte[] encryptedBytes = msEncrypt.ToArray();
                    string encryptedText = Convert.ToBase64String(encryptedBytes);

                    // Ensure the encrypted text is at least 10 characters long
                    while (encryptedText.Length < 10)
                    {
                        encryptedText += "0"; // You can choose a different padding character if needed
                    }

                    return encryptedText;
                }
            }
        }
        public static async UniTask DelayCall(float sec, Action action, bool ignoreTimeScale, CancellationToken token = default)
        {
            token = token == default ? GameManager.Instance.destroyCancellationToken : token;
            await UniTask.WaitForSeconds(sec, ignoreTimeScale, PlayerLoopTiming.Update, token);
            action.Invoke();
        }
        public static Vector3 Vector3FromAngle(float degrees, float magnitude)
        {
            float radians = degrees * Mathf.Deg2Rad;
            return new Vector3(magnitude * Mathf.Cos(radians), magnitude * Mathf.Sin(radians), 0);
        }
        public static long GetTimeStampFromDateTime(DateTime value)
        {
            return ((DateTimeOffset)value).ToUnixTimeSeconds();
        }

        public static DateTime GetDateTimeFromTimeStampSec(long value)
        {
            DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dt = dt.AddSeconds(value).ToLocalTime();
            return dt;
        }
        public static DateTime GetDateTimeFromTimeStampMillisec(long value)
        {
            DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dt = dt.AddMilliseconds(value).ToLocalTime();
            return dt;
        }
        public static object BuiltinLoadAssetAtPath(Type assetType, string absolutePath)
        {
            if (assetType == typeof(byte[]))
            {
                return File.ReadAllBytes(absolutePath);
            }
            else if (assetType == typeof(string))
            {
                return File.ReadAllText(absolutePath);
            }
            else if (assetType == typeof(Texture2D))
            {
                var texture = new Texture2D(1, 1);
                texture.LoadImage(File.ReadAllBytes(absolutePath));
                return texture;
            }
            throw new NotSupportedException();
        }
    }
}