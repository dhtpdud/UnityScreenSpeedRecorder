using OSY;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

public class GameManager : Singleton<GameManager>
{
    public int ScreenWidth;
    public int ScreenHeight;
    [HideInInspector]
    public Rect ScreenRect;

    //캐싱용 변수
    public float deltaTime { get; private set; }
    public float captureDeltaTime { get; private set; }
    public float unscaledDeltaTime { get; private set; }
    public float targetFrameRate { get; private set; }
    public float timeScale { get; private set; }
    public float realTimeScale { get; private set; }

    public int originVSyncCount { get; private set; }
    public int originTargetFramerate { get; private set; }
    public int origincaptureFramerate { get; private set; }

    public Camera mainCam;

    //함수 return 이후, 남은 변수들을 GC가 자동으로 GC.Collect()를 실행하여 실시간으로 처리함
    //문제는 해당 함수의 비용이 어마어마 하다는 것....
    //따라서, 이것을 해결하기 위해 쓰래기통 리스트에 다쓴 변수들을 잠시 보관했다가,
    //원하는 순간에 Clear를 호출하여, 그떄 GC가 처리하도록 함.
    public readonly List<object> TrashCan = new List<object>();

    public Dictionary<string, Texture2D> thumbnailsCacheDic = new Dictionary<string, Texture2D>();
    [ReadOnly(false)]
    public Vector2 onMouseDownPosition;
    [ReadOnly(false)]
    public Vector2 onMouseDragPosition;
    [ReadOnly(false)]
    public GameObject dragingObject;

    [HideInInspector]
    public string instanceToken;

    protected override void Awake()
    {
        base.Awake();
        Time.timeScale = 1;
        Screen.SetResolution(ScreenWidth, ScreenHeight, false);
        ScreenRect = new Rect(0, 0, ScreenWidth, ScreenHeight);
        Profiler.maxUsedMemory = 2000000000;//2GB
        originTargetFramerate = Application.targetFrameRate;
        origincaptureFramerate = Time.captureFramerate;
        originVSyncCount = QualitySettings.vSyncCount;
        mainCam ??= Camera.main;
        mainCam.enabled = true;
    }
    private void Update()
    {
        deltaTime = Time.deltaTime;
        targetFrameRate = Application.targetFrameRate;
        captureDeltaTime = Time.captureDeltaTime;
        timeScale = Time.timeScale;
        unscaledDeltaTime = Time.unscaledDeltaTime;
        realTimeScale = deltaTime / unscaledDeltaTime;
        //OSY.Debug.Log("timeScale: " + timeScale + "realTimeScale: " + deltaTime + "/" + unscaledDeltaTime);
        if (mainCam != null)
        {
            if (Input.GetMouseButtonDown(0))
                onMouseDownPosition = mainCam.ScreenToWorldPoint(Input.mousePosition);
            if (Input.GetMouseButton(0))
                onMouseDragPosition = mainCam.ScreenToWorldPoint(Input.mousePosition);
        }
    }
    private void OnDestroy()
    {
        if (mainCam != null)
        {
            Destroy(mainCam);
        }
        FindObjectsByType<Canvas>(FindObjectsSortMode.None).ForEach(Canvas => Canvas.worldCamera = Instance.mainCam);
    }
    public void OpenPage(string url)
    {
        Application.OpenURL(url);
    }
    public void ShutDown()
    {
        Application.Quit();
    }
}
