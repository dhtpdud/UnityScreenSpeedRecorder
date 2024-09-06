using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace OSY
{
    public static class OSYExtensions
    {
        public static T PickRandomly<T>(this IEnumerable<T> target) => target.ElementAt(OSYUtils.GetRandom(0, target.Count()));
        public static T ToEnum<T>(this string e) => (T)Enum.Parse(typeof(T), e);
        public static void CancelAndDispose(this CancellationTokenSource tokenSource)
        {
            try
            {
                tokenSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {

            }
            finally
            {
                tokenSource?.Dispose();
            }
        }
        public static T GetValueOrDefault<T>(this JToken jToken, string key, T defaultValue = default(T))
        {
            JToken value = jToken[key];
            return (T)(value != null ? value.ToObject<T>() : defaultValue);
        }
        public static T AddTrashCan<T>(this T obj)
        {
            GameManager.Instance.TrashCan.Add(obj);
            return obj;
        }
        public static IEnumerable<T> ForEach<T>(this IEnumerable<T> ienumerable, Action<T> action)
        {
            foreach (T item in ienumerable)
                action(item);
            return ienumerable;
        }
        public static async UniTask<CancellationTokenSource> CancelAndRecycle(this CancellationTokenSource tokenSource, CancellationToken parentToken = default)
        {
            parentToken = parentToken == default ? GameManager.Instance.destroyCancellationToken : parentToken;
            tokenSource.CancelAndDispose();
            await UniTask.Yield();
            return CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        }
        public static void DisposeAll(this IEnumerable<IDisposable> disposables)
        {
            foreach (IDisposable disposable in disposables)
                disposable.Dispose();
        }
        public static void Add<T>(this List<T> list, params T[] items) => list.AddRange(items);
        public static void AllForget(this IEnumerable<UniTask> tasks) => tasks.ForEach(task => task.Forget());

        public static void AllForget(this IEnumerable<UniTaskVoid> tasks) => tasks.ForEach(task => task.Forget());


        public static async UniTask WriteJsonFile(this JObject jobject, string path, CancellationToken token)
        {
            using (StreamWriter file = File.CreateText(path))
            using (JsonTextWriter writer = new JsonTextWriter(file))
            {
                await jobject.WriteToAsync(writer);
            }
        }
        public static JToken ToJObject(this IEnumerable<JToken> enumerable) => JToken.FromObject(enumerable);
        public static Sprite ToSprite(this Texture2D texture)
        {
            Rect rect = new Rect(0, 0, texture.width, texture.height);
            return Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f));
        }
    }
}