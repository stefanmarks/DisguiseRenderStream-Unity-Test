﻿#if UNITY_STANDALONE_WIN 
#define PLUGIN_AVAILABLE
#endif

using System;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;

using Microsoft.Win32;

using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using UnityEngine.SceneManagement;
using System.Threading;
using Disguise.RenderStream;
using System.Linq;

#if UNITY_EDITOR
class DisguiseRenderStream  : UnityEditor.Build.IPreprocessBuildWithReport
#else
class DisguiseRenderStream
#endif
{
#if UNITY_EDITOR
    public int callbackOrder { get { return 0; } }
    public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
    {
        DisguiseRenderStreamSettings settings = DisguiseRenderStreamSettings.GetOrCreateSettings();            
        schema = new ManagedSchema();
        schema.channels = new string[0];
        switch (settings.sceneControl)
        {
            case DisguiseRenderStreamSettings.SceneControl.Selection:
                Debug.Log("Generating scene-selection schema for: " + SceneManager.sceneCountInBuildSettings + " scenes");
                schema.scenes = new ManagedRemoteParameters[SceneManager.sceneCountInBuildSettings];
                if (SceneManager.sceneCountInBuildSettings == 0)
                    Debug.LogWarning("No scenes in build settings. Schema will be empty.");
                break;
            case DisguiseRenderStreamSettings.SceneControl.Manual:
            default:
                Debug.Log("Generating manual schema");
                schema.scenes = new ManagedRemoteParameters[1];
                break;
        }
    }

    [UnityEditor.Callbacks.PostProcessSceneAttribute(0)]
    static void OnPostProcessScene()
    {
        if (!UnityEditor.BuildPipeline.isBuildingPlayer)
            return;

        Scene activeScene = SceneManager.GetActiveScene();
        DisguiseRenderStreamSettings settings = DisguiseRenderStreamSettings.GetOrCreateSettings();            
        switch (settings.sceneControl)
        {
            case DisguiseRenderStreamSettings.SceneControl.Selection:
            {
                if (activeScene.buildIndex < 0 || activeScene.buildIndex >= SceneManager.sceneCountInBuildSettings)
                {
                    Debug.Log("Ignoring scene: " + activeScene.name + " (not in build's scene list)");
                    return;
                }

                Debug.Log("Processing scene: " + activeScene.name + " (" + activeScene.buildIndex + '/' + SceneManager.sceneCountInBuildSettings + ')');

                HashSet<string> channels = new HashSet<string>(schema.channels);
                channels.UnionWith(getTemplateCameras().Select(camera => camera.name));
                schema.channels = channels.ToArray();

                List<ManagedRemoteParameter> parameters = new List<ManagedRemoteParameter>();
                foreach (var parameter in UnityEngine.Object.FindObjectsOfType(typeof(DisguiseRemoteParameters)) as DisguiseRemoteParameters[])
                    parameters.AddRange(parameter.exposedParameters());
                schema.scenes[activeScene.buildIndex] = new ManagedRemoteParameters();
                ManagedRemoteParameters scene = schema.scenes[activeScene.buildIndex];
                scene.name = activeScene.name;
                scene.parameters = parameters.ToArray();
                break;
            }
            case DisguiseRenderStreamSettings.SceneControl.Manual:
            default:
            {
                Debug.Log("Processing scene: " + activeScene.name);

                HashSet<string> channels = new HashSet<string>(schema.channels);
                channels.UnionWith(getTemplateCameras().Select(camera => camera.name));
                schema.channels = channels.ToArray();

                if (schema.scenes[0] == null)
                {
                    schema.scenes[0] = new ManagedRemoteParameters();
                    schema.scenes[0].parameters = new ManagedRemoteParameter[0];
                }
                ManagedRemoteParameters scene = schema.scenes[0];
                scene.name = "Default";
                List<ManagedRemoteParameter> parameters = new List<ManagedRemoteParameter>(scene.parameters);
                foreach (var parameter in UnityEngine.Object.FindObjectsOfType(typeof(DisguiseRemoteParameters)) as DisguiseRemoteParameters[])
                    parameters.AddRange(parameter.exposedParameters());
                scene.parameters = parameters.ToArray();
                break;
            }
        }
    }

    [UnityEditor.Callbacks.PostProcessBuildAttribute(0)]
    static void OnPostProcessBuild(UnityEditor.BuildTarget target, string pathToBuiltProject)
    {
        if (target != UnityEditor.BuildTarget.StandaloneWindows64)
        {
            Debug.LogError("DisguiseRenderStream: RenderStream is only available for 64-bit Windows (x86_64).");
            return;
        }

        if (PluginEntry.instance.IsAvailable == false)
        {
            Debug.LogError("DisguiseRenderStream: RenderStream DLL not available, could not save schema");
            return;
        }

        RS_ERROR error = PluginEntry.instance.saveSchema(pathToBuiltProject, ref schema);
        if (error != RS_ERROR.RS_ERROR_SUCCESS)
        {
            Debug.LogError(string.Format("DisguiseRenderStream: Failed to save schema {0}", error));
        }
    }
#endif

    [RuntimeInitializeOnLoadMethod]
    static void OnLoad()
    {
        if (Application.isEditor)
        {
            // No play in editor support currently
            return;
        }

        if (PluginEntry.instance.IsAvailable == false)
        {
            Debug.LogError("DisguiseRenderStream: RenderStream DLL not available");
            return;
        }

        string pathToBuiltProject = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
        RS_ERROR error = PluginEntry.instance.loadSchema(pathToBuiltProject, ref schema);
        if (error == RS_ERROR.RS_ERROR_SUCCESS)
        {
            sceneFields = new SceneFields[schema.scenes.Length];
            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.isLoaded)
                OnSceneLoaded(activeScene, LoadSceneMode.Single);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }
        else
        {
            Debug.LogError(string.Format("DisguiseRenderStream: Failed to load schema {0}", error));
            schema = new ManagedSchema();
            schema.channels = new string[0];
            schema.scenes = new ManagedRemoteParameters[1];
            schema.scenes[0] = new ManagedRemoteParameters();
            schema.scenes[0].name = "Default";
            schema.scenes[0].parameters = new ManagedRemoteParameter[0];
            sceneFields = new SceneFields[schema.scenes.Length];
            CreateStreams();
        }
    }

    static void OnSceneLoaded(Scene loadedScene, LoadSceneMode mode)
    {
        CreateStreams();
        int sceneIndex = 0;
        DisguiseRenderStreamSettings settings = DisguiseRenderStreamSettings.GetOrCreateSettings();
        switch (settings.sceneControl)
        {
            case DisguiseRenderStreamSettings.SceneControl.Selection:
                sceneIndex = loadedScene.buildIndex;
                break;
        }
        DisguiseRemoteParameters[] remoteParameters = UnityEngine.Object.FindObjectsOfType(typeof(DisguiseRemoteParameters)) as DisguiseRemoteParameters[];
        ManagedRemoteParameters scene = schema.scenes[sceneIndex];
        sceneFields[sceneIndex] = new SceneFields{ numerical = new List<ObjectField>(), images = new List<ObjectField>(), texts = new List<ObjectField>() };
        SceneFields fields = sceneFields[sceneIndex];
        for (int j = 0; j < scene.parameters.Length;)
        {
            string key = scene.parameters[j].key;
            DisguiseRemoteParameters remoteParams = Array.Find(remoteParameters, rp => key.StartsWith(rp.prefix));
            ObjectField field = new ObjectField();
            field.target = remoteParams.exposedObject;
            field.info = null;
            if (field.info == null && key.EndsWith("_x"))
            {
                string baseKey = key.Substring(0, key.Length - 2);
                field.info = remoteParams.GetMemberInfoFromPropertyPath(baseKey.Substring(remoteParams.prefix.Length + 1));
                Type fieldType = field.FieldType;
                if ((fieldType == typeof(Vector2) || fieldType == typeof(Vector2Int)) &&
                    j + 1 < scene.parameters.Length && scene.parameters[j + 1].key == baseKey + "_y")
                {
                    j += 2;
                }
                else if ((fieldType == typeof(Vector3) || fieldType == typeof(Vector3Int)) &&
                    j + 2 < scene.parameters.Length && scene.parameters[j + 1].key == baseKey + "_y" && scene.parameters[j + 2].key == baseKey + "_z")
                {
                    j += 3;
                }
                else if (fieldType == typeof(Vector4) &&
                    j + 3 < scene.parameters.Length && scene.parameters[j + 1].key == baseKey + "_y" && scene.parameters[j + 2].key == baseKey + "_z" && scene.parameters[j + 3].key == baseKey + "_w")
                {
                    j += 4;
                }
                else
                {
                    field.info = null;
                }
            }
            if (field.info == null && key.EndsWith("_r"))
            {
                string baseKey = key.Substring(0, key.Length - 2);
                field.info = remoteParams.GetMemberInfoFromPropertyPath(baseKey.Substring(remoteParams.prefix.Length + 1));
                Type fieldType = field.FieldType;
                if (fieldType == typeof(Color) &&
                    j + 3 < scene.parameters.Length && scene.parameters[j + 1].key == baseKey + "_g" && scene.parameters[j + 2].key == baseKey + "_b" && scene.parameters[j + 3].key == baseKey + "_a")
                {
                    j += 4;
                }
                else
                {
                    field.info = null;
                }
            }
            if (field.info == null)
            {
                field.info = remoteParams.GetMemberInfoFromPropertyPath(key.Substring(remoteParams.prefix.Length + 1));
                ++j;
            }
            if (field.info == null)
            {
                Debug.LogError("Unhandled remote parameter: " + key);
            }

            if (field.FieldType == typeof(Texture))
                fields.images.Add(field);
            else if (field.FieldType == typeof(String) || field.FieldType == typeof(String[]))
                fields.texts.Add(field);
            else
                fields.numerical.Add(field);
        }
    }

    static void CreateStreams()
    {
        if (PluginEntry.instance.IsAvailable == false)
        {
            Debug.LogError("DisguiseRenderStream: RenderStream DLL not available");
            return;
        }

        do
        {
            RS_ERROR error = PluginEntry.instance.getStreams(ref streams);
            if (error != RS_ERROR.RS_ERROR_SUCCESS)
            {
                Debug.LogError(string.Format("DisguiseRenderStream: Failed to get streams {0}", error));
                return;
            }

            if (streams.Length == 0)
            {
                Debug.Log("Waiting for streams...");
                Thread.Sleep(1000);
            }
        } while (streams.Length == 0);

        Debug.Log(string.Format("Found {0} streams", streams.Length));
        foreach (var camera in cameras)
            UnityEngine.Object.Destroy(camera);
        cameras = new GameObject[streams.Length];

        // cache the template cameras prior to instantiating our instance cameras 
        Camera[] templateCameras = getTemplateCameras();
        const int cullUIOnly = ~(1 << 5);

        for (int i = 0; i < streams.Length; ++i)
        {        
            StreamDescription stream = streams[i];
            Camera channelCamera = DisguiseRenderStream.GetChannelCamera(stream.channel);
            if (channelCamera)
            {
                cameras[i] = UnityEngine.Object.Instantiate(channelCamera.gameObject, channelCamera.gameObject.transform.parent);
                cameras[i].name = stream.name;
            }
            else if (Camera.main)
            {
                cameras[i] = UnityEngine.Object.Instantiate(Camera.main.gameObject, Camera.main.gameObject.transform.parent);
                cameras[i].name = stream.name;
            }
            else
            {
                cameras[i] = new GameObject(stream.name);
                cameras[i].AddComponent<Camera>();
            }
            
            GameObject cameraObject = cameras[i];
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.enabled = true; // ensure the camera component is enable
            camera.cullingMask &= cullUIOnly; // cull the UI so RenderStream and other error messages don't render to RenderStream outputs
            DisguiseCameraCapture capture = cameraObject.GetComponent(typeof(DisguiseCameraCapture)) as DisguiseCameraCapture;
            if (capture == null)
                capture = cameraObject.AddComponent(typeof(DisguiseCameraCapture)) as DisguiseCameraCapture;
// Blocks HDRP streams in r18.2
// #if UNITY_PIPELINE_HDRP
//             Volume volume = cameraObject.GetComponent<Volume>();
//             if (volume == null)
//                 volume = cameraObject.AddComponent<Volume>();
//             volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
//             var captureAfterPostProcess = volume.profile.Add<DisguiseCameraCaptureAfterPostProcess>(true);
//             captureAfterPostProcess.width.value = (Int32)stream.width;
//             captureAfterPostProcess.height.value = (Int32)stream.height;
// #endif
            camera.enabled = true;
        }

        // stop template cameras impacting performance
        foreach (var templateCam in templateCameras)
        {
            templateCam.enabled = false; // disable the camera component on the template camera so these cameras won't render and impact performance
            // we don't want to disable the game object otherwise we won't be able to find the object again to instantiate instance cameras if we get a streams changed event
        }

        frameData = new FrameData();
        awaiting = false;
    }

    static public IEnumerator AwaitFrame()
    {
        if (awaiting)
            yield break;
        awaiting = true;
        DisguiseRenderStreamSettings settings = DisguiseRenderStreamSettings.GetOrCreateSettings();
        List<Texture2D> scratchTextures = new List<Texture2D>();
        while (true)
        {
            yield return new WaitForEndOfFrame();
            RS_ERROR error = PluginEntry.instance.awaitFrameData(500, ref frameData);
            if (error == RS_ERROR.RS_ERROR_QUIT)
                Application.Quit();
            if (error == RS_ERROR.RS_ERROR_STREAMS_CHANGED)
                CreateStreams();
            switch (settings.sceneControl)
            {
                case DisguiseRenderStreamSettings.SceneControl.Selection:
                    if (SceneManager.GetActiveScene().buildIndex != frameData.scene)
                    {
                        newFrameData = false;
                        SceneManager.LoadScene((int)frameData.scene);
                        yield break;
                    }
                    break;
            }
            newFrameData = (error == RS_ERROR.RS_ERROR_SUCCESS);
            if (newFrameData && frameData.scene < schema.scenes.Length)
            {
                ManagedRemoteParameters spec = schema.scenes[frameData.scene];
                SceneFields fields = sceneFields[frameData.scene];
                int nNumericalParameters = 0;
                int nImageParameters = 0;
                int nTextParameters = 0;
                for (int i = 0; i < spec.parameters.Length; ++i)
                {
                    if (spec.parameters[i].type == RemoteParameterType.RS_PARAMETER_NUMBER)
                        ++nNumericalParameters;
                    else if (spec.parameters[i].type == RemoteParameterType.RS_PARAMETER_IMAGE)
                        ++nImageParameters;
                    else if (spec.parameters[i].type == RemoteParameterType.RS_PARAMETER_POSE || spec.parameters[i].type == RemoteParameterType.RS_PARAMETER_TRANSFORM)
                        nNumericalParameters += 16;
                    else if (spec.parameters[i].type == RemoteParameterType.RS_PARAMETER_TEXT)
                        ++nTextParameters;
                }
                float[] parameters = new float[nNumericalParameters];
                ImageFrameData[] imageData = new ImageFrameData[nImageParameters];
                if (PluginEntry.instance.getFrameParameters(spec.hash, ref parameters) == RS_ERROR.RS_ERROR_SUCCESS && PluginEntry.instance.getFrameImageData(spec.hash, ref imageData) == RS_ERROR.RS_ERROR_SUCCESS)
                {
                    if (fields.numerical != null)
                    {
                        int i = 0;
                        foreach (var field in fields.numerical)
                        {
                            Type fieldType = field.FieldType;
                            if (fieldType.IsEnum)
                            {
                                field.SetValue(Enum.ToObject(fieldType, Convert.ToUInt64(parameters[i])));
                                ++i;
                            }
                            else if (fieldType == typeof(Vector2))
                            {
                                Vector2 v = new Vector2(parameters[i + 0], parameters[i + 1]);
                                field.SetValue(v);
                                i += 2;
                            }
                            else if (fieldType == typeof(Vector2Int))
                            {
                                Vector2Int v = new Vector2Int((int)parameters[i + 0], (int)parameters[i + 1]);
                                field.SetValue(v);
                                i += 2;
                            }
                            else if (fieldType == typeof(Vector3))
                            {
                                Vector3 v = new Vector3(parameters[i + 0], parameters[i + 1], parameters[i + 2]);
                                field.SetValue(v);
                                i += 3;
                            }
                            else if (fieldType == typeof(Vector3Int))
                            {
                                Vector3Int v = new Vector3Int((int)parameters[i + 0], (int)parameters[i + 1], (int)parameters[i + 2]);
                                field.SetValue(v);
                                i += 3;
                            }
                            else if (fieldType == typeof(Vector4))
                            {
                                Vector4 v = new Vector4(parameters[i + 0], parameters[i + 1], parameters[i + 2], parameters[i + 3]);
                                field.SetValue(v);
                                i += 4;
                            }
                            else if (fieldType == typeof(Color))
                            {
                                Color v = new Color(parameters[i + 0], parameters[i + 1], parameters[i + 2], parameters[i + 3]);
                                field.SetValue(v);
                                i += 4;
                            }
                            else if (fieldType == typeof(Transform))
                            {
                                Matrix4x4 m = new Matrix4x4();
                                m.SetColumn(0, new Vector4(parameters[i + 0],  parameters[i + 1],  parameters[i + 2],  parameters[i + 3]));
                                m.SetColumn(1, new Vector4(parameters[i + 4],  parameters[i + 5],  parameters[i + 6],  parameters[i + 7]));
                                m.SetColumn(2, new Vector4(parameters[i + 8],  parameters[i + 9],  parameters[i + 10], parameters[i + 11]));
                                m.SetColumn(3, new Vector4(parameters[i + 12], parameters[i + 13], parameters[i + 14], parameters[i + 15]));
                                Transform transform = field.GetValue() as Transform;
                                transform.localPosition = new Vector3(m[0, 3], m[1, 3], m[2, 3]);
                                transform.localScale = m.lossyScale;
                                transform.localRotation = m.rotation;
                                i += 16;
                            }
                            else
                            {
                                if (field.info != null)
                                    field.SetValue(Convert.ChangeType(parameters[i], fieldType));
                                ++i;
                            }
                        }
                    }
                    if (fields.images != null)
                    {
                        while (scratchTextures.Count < imageData.Length)
                        {
                            int index = scratchTextures.Count;
                            scratchTextures.Add(new Texture2D((int)imageData[index].width, (int)imageData[index].height, PluginEntry.ToTextureFormat(imageData[index].format), false, true));
                        }
                        uint i = 0;
                        foreach (var field in fields.images)
                        {
                            if (field.GetValue() is RenderTexture renderTexture)
                            {
                                Texture2D texture = scratchTextures[(int)i];
                                if (texture.width != imageData[i].width || texture.height != imageData[i].height || texture.format != PluginEntry.ToTextureFormat(imageData[i].format))
                                {
                                    scratchTextures[(int)i] = new Texture2D((int)imageData[i].width, (int)imageData[i].height, PluginEntry.ToTextureFormat(imageData[i].format), false, true);
                                    texture = scratchTextures[(int)i];
                                }
                                if (PluginEntry.instance.getFrameImage(imageData[i].imageId, ref texture) == RS_ERROR.RS_ERROR_SUCCESS)
                                {
                                    texture.IncrementUpdateCount();
                                    Graphics.Blit(texture, renderTexture, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 1.0f));
                                    renderTexture.IncrementUpdateCount();
                                }
                            }
                            ++i;
                        }
                    }
                    if (fields.texts != null)
                    {
                        uint i = 0;
                        foreach (var field in fields.texts)
                        {
                            string text = "";
                            if (PluginEntry.instance.getFrameText(spec.hash, i, ref text) == RS_ERROR.RS_ERROR_SUCCESS)
                            {
                                if (field.FieldType == typeof(String[]))
                                    field.SetValue(text.Split(' '));
                                else
                                    field.SetValue(text);
                            }
                        }
                        ++i;
                    }
                }
            }
        }
    }

    static Camera[] getTemplateCameras()
    {
        return Camera.allCameras;
    }

    static Camera GetChannelCamera(string channel)
    {
        try
        {
            return Array.Find(getTemplateCameras(), camera => camera.name == channel);
        }
        catch (ArgumentNullException)
        {
            return Camera.main;
        }
    }

    static public StreamDescription[] streams = { };
    static public bool awaiting = false;
    static public FrameData frameData;
    static public bool newFrameData = false;

    static private GameObject[] cameras = { };
    static private ManagedSchema schema = new ManagedSchema();
    public class ObjectField
    {
        public object target;
        public MemberInfo info;
        public Type FieldType { 
            get {
                if (info is FieldInfo fieldInfo)
                    return fieldInfo.FieldType;
                else if (info is PropertyInfo propertyInfo)
                    return propertyInfo.PropertyType;
                return typeof(void);
            }
        }
        public void SetValue(object value)
        {
            if (info is FieldInfo fieldInfo)
                fieldInfo.SetValue(target, value);
            else if (info is PropertyInfo propertyInfo)
                propertyInfo.SetValue(target, value);
        }
        public object GetValue()
        {
            if (info is FieldInfo fieldInfo)
                return fieldInfo.GetValue(target);
            else if (info is PropertyInfo propertyInfo)
                return propertyInfo.GetValue(target);
            return null;
        }
    }
    public struct SceneFields
    {
        public List<ObjectField> numerical;
        public List<ObjectField> images;
        public List<ObjectField> texts;
    }
    static private SceneFields[] sceneFields = new SceneFields[0];
}

[AddComponentMenu("")]
[RequireComponent(typeof(Camera))]
public class DisguiseCameraCapture : MonoBehaviour
{
    // Start is called before the first frame update
    public IEnumerator Start()
    {
        if (PluginEntry.instance.IsAvailable == false)
        {
            Debug.LogError("DisguiseCameraCapture: RenderStream DLL not available, capture cannot start.");
            enabled = false;
            yield break;
        }

        m_cameraData = new CameraData();

        m_camera = GetComponent<Camera>();
        m_frameSender = new Disguise.RenderStream.FrameSender(gameObject.name, m_camera);
        RenderPipelineManager.endFrameRendering += RenderPipelineManager_endFrameRendering;

        if (Application.isPlaying == false)
            yield break;

        if (!DisguiseRenderStream.awaiting)
            yield return StartCoroutine(DisguiseRenderStream.AwaitFrame());
    }

    // Update is called once per frame
    public void Update()
    {
        // set tracking
        m_newFrameData = DisguiseRenderStream.newFrameData && m_frameSender != null && m_frameSender.GetCameraData(ref m_cameraData);
        float cameraAspect = m_camera.aspect;
        Vector2 lensShift = new Vector2(0.0f, 0.0f);
        if (m_newFrameData)
        {
            cameraAspect = m_cameraData.sensorX / m_cameraData.sensorY;
			if (m_cameraData.cameraHandle != 0)  // If no camera, only set aspect
			{
				transform.localPosition = new Vector3(m_cameraData.x, m_cameraData.y, m_cameraData.z);
				transform.localRotation = Quaternion.Euler(new Vector3(-m_cameraData.rx, m_cameraData.ry, -m_cameraData.rz));
				m_camera.nearClipPlane = m_cameraData.nearZ;
				m_camera.farClipPlane = m_cameraData.farZ;

				if (m_cameraData.orthoWidth > 0.0f)  // Use an orthographic camera
				{  
					m_camera.orthographic = true;
					m_camera.orthographicSize = 0.5f * m_cameraData.orthoWidth / cameraAspect;
					transform.localPosition = new Vector3(m_cameraData.x, m_cameraData.y, m_cameraData.z);
					transform.localRotation = Quaternion.Euler(new Vector3(-m_cameraData.rx, m_cameraData.ry, -m_cameraData.rz));
				}
				else  // Perspective projection, use camera lens properties
				{
					m_camera.usePhysicalProperties = true;
					m_camera.sensorSize = new Vector2(m_cameraData.sensorX, m_cameraData.sensorY);
					m_camera.focalLength = m_cameraData.focalLength;
					lensShift = new Vector2(-m_cameraData.cx, m_cameraData.cy);
				}
			}
        }
        else if (m_frameSender != null)
        {
            // By default aspect is resolution aspect. We need to undo the effect of the subregion on this to get the whole image aspect.
            cameraAspect = m_camera.aspect * (m_frameSender.subRegion.height / m_frameSender.subRegion.width);
        }

        // Clip to correct subregion and calculate projection matrix
        if (m_frameSender != null)
        {
            Rect subRegion = m_frameSender.subRegion;
            
            float imageHeight, imageWidth;
            if (m_camera.orthographic)
            {
                imageHeight = 2.0f * m_camera.orthographicSize;
                imageWidth = cameraAspect * imageHeight;
            }
            else
            {
                float fovV = m_camera.fieldOfView * Mathf.Deg2Rad;
                float fovH = Camera.VerticalToHorizontalFieldOfView(m_camera.fieldOfView, cameraAspect) * Mathf.Deg2Rad;
                imageWidth = 2.0f * (float)Math.Tan(0.5f * fovH);
                imageHeight = 2.0f * (float)Math.Tan(0.5f * fovV);
            }

            float l = (-0.5f + subRegion.xMin) * imageWidth;
            float r = (-0.5f + subRegion.xMax) * imageWidth;
            float t = (-0.5f + 1.0f - subRegion.yMin) * imageHeight;
            float b = (-0.5f + 1.0f - subRegion.yMax) * imageHeight;

            Matrix4x4 projectionMatrix;
            if (m_camera.orthographic)
                projectionMatrix = Matrix4x4.Ortho(l, r, b, t, m_camera.nearClipPlane, m_camera.farClipPlane);
            else
                projectionMatrix = PerspectiveOffCenter(l * m_camera.nearClipPlane, r * m_camera.nearClipPlane, b * m_camera.nearClipPlane, t * m_camera.nearClipPlane, m_camera.nearClipPlane, m_camera.farClipPlane);

            Matrix4x4 clippingTransform = Matrix4x4.Translate(new Vector3(-lensShift.x / subRegion.width, lensShift.y / subRegion.height, 0.0f));
            m_camera.projectionMatrix = clippingTransform * projectionMatrix;
        }
    }

    // From http://docs.unity3d.com/ScriptReference/Camera-projectionMatrix.html
    static Matrix4x4 PerspectiveOffCenter(float left, float right, float bottom, float top, float near, float far)
    {
        float x = 2.0F * near / (right - left);
        float y = 2.0F * near / (top - bottom);
        float a = (right + left) / (right - left);
        float b = (top + bottom) / (top - bottom);
        float c = -(far + near) / (far - near);
        float d = -(2.0F * far * near) / (far - near);
        float e = -1.0F;
        Matrix4x4 m = new Matrix4x4();
        m[0, 0] = x;
        m[0, 1] = 0;
        m[0, 2] = a;
        m[0, 3] = 0;
        m[1, 0] = 0;
        m[1, 1] = y;
        m[1, 2] = b;
        m[1, 3] = 0;
        m[2, 0] = 0;
        m[2, 1] = 0;
        m[2, 2] = c;
        m[2, 3] = d;
        m[3, 0] = 0;
        m[3, 1] = 0;
        m[3, 2] = e;
        m[3, 3] = 0;
        return m;
    }

    public void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        CheckAndSendFrame();
    }

    private void CheckAndSendFrame()
    {
        if (m_newFrameData)
        {
            if (m_frameSender != null)
                m_frameSender.SendFrame(DisguiseRenderStream.frameData, m_cameraData);
            m_newFrameData = false;
        }
    }

    private void RenderPipelineManager_endFrameRendering(ScriptableRenderContext context, Camera[] cameras)
    {
        foreach (var cam in cameras)
        {
            if (cam == m_camera)
                CheckAndSendFrame();
        }
    }

    public void OnDestroy()
    {
    }

    public void OnDisable()
    {
        if (m_frameSender != null)
        {
            m_frameSender.DestroyStream();
        }
        RenderPipelineManager.endFrameRendering -= RenderPipelineManager_endFrameRendering;
    }

    Camera m_camera;
    public Disguise.RenderStream.FrameSender m_frameSender;

    CameraData m_cameraData;
    bool m_newFrameData = false;
}

namespace Disguise.RenderStream
{
    // d3renderstream/d3renderstream.h
    using StreamHandle = UInt64;
    using CameraHandle = UInt64;
    delegate void logger_t(string message);

    public enum RSPixelFormat : UInt32
    {
        RS_FMT_INVALID,

        RS_FMT_BGRA8,
        RS_FMT_BGRX8,

        RS_FMT_RGBA32F,

        RS_FMT_RGBA16,

        RS_FMT_RGBA8,
        RS_FMT_RGBX8,
    }

    public enum SenderFrameType : UInt32
    {
        RS_FRAMETYPE_HOST_MEMORY = 0x00000000,
        RS_FRAMETYPE_DX11_TEXTURE,
        RS_FRAMETYPE_DX12_TEXTURE,
        RS_FRAMETYPE_OPENGL_TEXTURE,
		RS_FRAMETYPE_VULKAN_TEXTURE,
        RS_FRAMETYPE_UNKNOWN
    }

    public enum UseDX12SharedHeapFlag : UInt32
    {
        RS_DX12_USE_SHARED_HEAP_FLAG,
        RS_DX12_DO_NOT_USE_SHARED_HEAP_FLAG
    }

    public enum RS_ERROR : UInt32
    {
        RS_ERROR_SUCCESS = 0,

        // Core is not initialised
        RS_NOT_INITIALISED,

        // Core is already initialised
        RS_ERROR_ALREADYINITIALISED,

        // Given handle is invalid
        RS_ERROR_INVALIDHANDLE,

        // Maximum number of frame senders have been created
        RS_MAXSENDERSREACHED,

        RS_ERROR_BADSTREAMTYPE,

        RS_ERROR_NOTFOUND,

        RS_ERROR_INCORRECTSCHEMA,

        RS_ERROR_INVALID_PARAMETERS,

        RS_ERROR_BUFFER_OVERFLOW,

        RS_ERROR_TIMEOUT,

        RS_ERROR_STREAMS_CHANGED,

        RS_ERROR_INCOMPATIBLE_VERSION,

        RS_ERROR_FAILED_TO_GET_DXDEVICE_FROM_RESOURCE,

        RS_ERROR_FAILED_TO_INITIALISE_GPGPU,

        RS_ERROR_QUIT,

        RS_ERROR_UNSPECIFIED
    }

    // Bitmask flags
    public enum FRAMEDATA_FLAGS : UInt32
    {
        FRAMEDATA_NO_FLAGS = 0,
        FRAMEDATA_RESET = 1
    }

    public enum REMOTEPARAMETER_FLAGS : UInt32
    {
        REMOTEPARAMETER_NO_FLAGS = 0,
        REMOTEPARAMETER_NO_SEQUENCE = 1,
		REMOTEPARAMETER_READ_ONLY = 2
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
	public struct D3TrackingData
	{
		public byte virtualReprojectionRequired;
	}  // Tracking data required by d3 but not used to render content

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct CameraData
    {
        public StreamHandle streamHandle;
        public CameraHandle cameraHandle;
        public float x, y, z;
        public float rx, ry, rz;
        public float focalLength;
        public float sensorX, sensorY;
        public float cx, cy;
        public float nearZ, farZ;
        public float orthoWidth;  // If > 0, an orthographic camera should be used
		public float aperture; // Apply if > 0
		public float focusDistance;  // Apply if > 0
		public D3TrackingData d3Tracking;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FrameData
    {
        public double tTracked;
        public double localTime;
        public double localTimeDelta;
        public UInt32 frameRateNumerator;
        public UInt32 frameRateDenominator;
        public UInt32 flags; // FRAMEDATA_FLAGS
        public UInt32 scene;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct CameraResponseData
    {
        public double tTracked;
        public CameraData camera;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FrameResponseData
    {
        public /*CameraResponseData**/ IntPtr cameraData;
        public UInt64 schemaHash;
        public UInt64 parameterDataSize;
        public IntPtr parameterData;
        public UInt32 textDataCount;
        public /*const char***/ IntPtr textData;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct SenderFrame
    {
		[FieldOffset(0)]
		public SenderFrameType type;
		/*union*/ 
        // struct HostMemoryData
        [FieldOffset(4)]
        public /*uint8_t**/ IntPtr host_data;
        [FieldOffset(12)]
        public UInt32 host_stride;
        // struct Dx11Data
        [FieldOffset(4)]
        public /*struct ID3D11Resource**/ IntPtr dx11_resource;
        // struct Dx12Data
        [FieldOffset(4)]
        public /*struct ID3D12Resource**/ IntPtr dx12_resource;
        // struct OpenGlData
        [FieldOffset(4)]
        public /*GLuint**/ UInt32 gl_texture;
		// struct VulkanData
		[FieldOffset(4)]
		public /*VkDeviceMemory*/ IntPtr cameraData;
		[FieldOffset(12)]
		public /*VkDeviceSize*/ UInt64 size;
		[FieldOffset(20)]
		public RSPixelFormat format;
		[FieldOffset(24)]
		public UInt32 width;
		[FieldOffset(28)]
		public UInt32 height;
		[FieldOffset(32)]
		public /*VkSemaphore*/ IntPtr waitSemaphore;
		[FieldOffset(40)]
		public UInt64 waitSemaphoreValue;
		[FieldOffset(48)]
		public /*VkSemaphore*/ IntPtr signalSemaphore;
		[FieldOffset(56)]
		public UInt64 signalSemaphoreValue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FrameRegion
    {
        public UInt32 xOffset;
        public UInt32 yOffset;
        public UInt32 width;
        public UInt32 height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ProjectionClipping
    {
        public float left;
        public float right;
        public float top;
        public float bottom;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct StreamDescription
    {
        public StreamHandle handle;
        [MarshalAs(UnmanagedType.LPStr)]
        public string channel;
        public UInt64 mappingId;
        public Int32 iViewpoint;
        [MarshalAs(UnmanagedType.LPStr)]
        public string name;
        public UInt32 width;
        public UInt32 height;
        public RSPixelFormat format;
        public ProjectionClipping clipping;
		[MarshalAs(UnmanagedType.LPStr)]
        public string mappingName;
		public Int32 iFragment;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct StreamDescriptions
    {
        public UInt32 nStreams;
        public /*StreamDescription**/ IntPtr streams;
    }

    public enum RemoteParameterType : UInt32
    {
        RS_PARAMETER_NUMBER,
        RS_PARAMETER_IMAGE,
        RS_PARAMETER_POSE,      // 4x4 TR matrix
        RS_PARAMETER_TRANSFORM, // 4x4 TRS matrix
        RS_PARAMETER_TEXT,
		RS_PARAMETER_EVENT,
		RS_PARAMETER_SKELETON
    }

    public enum RemoteParameterDmxType : UInt32
    {
        RS_DMX_DEFAULT,
        RS_DMX_8,
        RS_DMX_16_BE,
    }

    [StructLayout(LayoutKind.Explicit)]
    public /*union*/ struct RemoteParameterTypeDefaults
    {
        [FieldOffset(0)]
        public float numerical_min;
        [FieldOffset(4)]
        public float numerical_max;
        [FieldOffset(8)]
        public float numerical_step;
        [FieldOffset(12)]
        public float numerical_defaultValue;
        [FieldOffset(0)]
        public /*const char**/ IntPtr text_defaultValue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ImageFrameData
    {
        public UInt32 width;
        public UInt32 height;
        public RSPixelFormat format;
        public Int64 imageId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct RemoteParameter
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string group;
        [MarshalAs(UnmanagedType.LPStr)]
        public string displayName;
        [MarshalAs(UnmanagedType.LPStr)]
        public string key;
        public RemoteParameterType type;
        public RemoteParameterTypeDefaults defaults;
        public UInt32 nOptions;
        public /*const char***/ IntPtr options;

        public Int32 dmxOffset; // DMX channel offset or auto (-1)
        public RemoteParameterDmxType dmxType;
        public UInt32 flags; // REMOTEPARAMETER_FLAGS
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct RemoteParameters
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string name;
        public UInt32 nParameters;
        public /*RemoteParameter**/ IntPtr parameters;
        public UInt64 hash;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Scenes
    {
        public UInt32 nScenes;
        public /*RemoteParameters**/ IntPtr scenes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Channels
    {
        public UInt32 nChannels;
        public /*const char***/ IntPtr channels;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Schema
    {
        [MarshalAs(UnmanagedType.LPStr)] 
        public string engineName;
        [MarshalAs(UnmanagedType.LPStr)]
        public string engineVersion;
		[MarshalAs(UnmanagedType.LPStr)] 
        public string pluginVersion;
        [MarshalAs(UnmanagedType.LPStr)]
        public string info;
        public Channels channels;
        public Scenes scenes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ProfilingEntry
    {
        public string name;
        public float value;
    }


    public class ManagedRemoteParameter
    {
        public string group;
        public string displayName;
        public string key;
        public RemoteParameterType type;
        public float min;
        public float max;
        public float step;
        public object defaultValue;
        public string[] options = { };

        public Int32 dmxOffset;
        public RemoteParameterDmxType dmxType;
    }

    public class ManagedRemoteParameters
    {
        public string name;
        public ManagedRemoteParameter[] parameters = { };
        public UInt64 hash;
    }

    public class ManagedSchema
    {
        public string[] channels = { };
        public ManagedRemoteParameters[] scenes = { };
    }

    [Serializable]
    public sealed class PluginEntry
    {
        private class Nested
        {
            // Explicit static constructor to tell C# compiler
            // not to mark type as beforefieldinit
            static Nested() { }

            internal static readonly PluginEntry instance = new PluginEntry();
        }

        public static PluginEntry instance { get { return Nested.instance; } }

        public static TextureFormat ToTextureFormat(RSPixelFormat fmt)
        {
            switch (fmt)
            {
                case RSPixelFormat.RS_FMT_BGRA8: return TextureFormat.BGRA32;
                case RSPixelFormat.RS_FMT_BGRX8: return TextureFormat.BGRA32;
                case RSPixelFormat.RS_FMT_RGBA32F: return TextureFormat.RGBAFloat;
                case RSPixelFormat.RS_FMT_RGBA16: return TextureFormat.RGBAFloat;
                case RSPixelFormat.RS_FMT_RGBA8: return TextureFormat.RGBA32;
                case RSPixelFormat.RS_FMT_RGBX8: return TextureFormat.RGBA32;
                default: return TextureFormat.BGRA32;
            }
        }

        public static RenderTextureFormat ToRenderTextureFormat(RSPixelFormat fmt)
        {
            switch (fmt)
            {
                case RSPixelFormat.RS_FMT_BGRA8: return RenderTextureFormat.ARGBFloat;
                case RSPixelFormat.RS_FMT_BGRX8: return RenderTextureFormat.ARGBFloat;
                case RSPixelFormat.RS_FMT_RGBA32F: return RenderTextureFormat.ARGBFloat;
                case RSPixelFormat.RS_FMT_RGBA16: return RenderTextureFormat.ARGBFloat;
                case RSPixelFormat.RS_FMT_RGBA8: return RenderTextureFormat.ARGBFloat;
                case RSPixelFormat.RS_FMT_RGBX8: return RenderTextureFormat.ARGBFloat;
                default: return RenderTextureFormat.ARGBFloat;
            }
        }

        // isolated functions, do not require init prior to use
        unsafe delegate void pRegisterLogFunc(logger_t logger);
        unsafe delegate void pUnregisterLogFunc();

        unsafe delegate RS_ERROR pInitialise(int expectedVersionMajor, int expectedVersionMinor);
        unsafe delegate RS_ERROR pInitialiseGpGpuWithoutInterop(/*ID3D11Device**/ IntPtr device);
        unsafe delegate RS_ERROR pInitialiseGpGpuWithDX11Device(/*ID3D11Device**/ IntPtr device);
        unsafe delegate RS_ERROR pInitialiseGpGpuWithDX11Resource(/*ID3D11Resource**/ IntPtr resource);
        unsafe delegate RS_ERROR pInitialiseGpGpuWithDX12DeviceAndQueue(/*ID3D12Device**/ IntPtr device, /*ID3D12CommandQueue**/ IntPtr queue);
        unsafe delegate RS_ERROR pInitialiseGpGpuWithOpenGlContexts(/*HGLRC**/ IntPtr glContext, /*HDC**/ IntPtr deviceContext);
        unsafe delegate RS_ERROR pInitialiseGpGpuWithVulkanDevice(/*VkDevice**/ IntPtr device);
        unsafe delegate RS_ERROR pShutdown();

        // non-isolated functions, these require init prior to use

        unsafe delegate RS_ERROR pUseDX12SharedHeapFlag(ref UseDX12SharedHeapFlag flag);
        unsafe delegate RS_ERROR pSaveSchema(string assetPath, /*Schema**/ IntPtr schema); // Save schema for project file/custom executable at (assetPath)
        unsafe delegate RS_ERROR pLoadSchema(string assetPath, /*Out*/ /*Schema**/ IntPtr schema, /*InOut*/ ref UInt32 nBytes); // Load schema for project file/custom executable at (assetPath) into a buffer of size (nBytes) starting at (schema)

        // workload functions, these require the process to be running inside d3's asset launcher environment

        unsafe delegate RS_ERROR pSetSchema(/*InOut*/ /*Schema**/ IntPtr schema); // Set schema and fill in per-scene hash for use with rs_getFrameParameters

        unsafe delegate RS_ERROR pGetStreams(/*Out*/ /*StreamDescriptions**/ IntPtr streams, /*InOut*/ ref UInt32 nBytes); // Populate streams into a buffer of size (nBytes) starting at (streams)

        unsafe delegate RS_ERROR pAwaitFrameData(int timeoutMs, /*Out*/ /*FrameData**/ IntPtr data);  // waits for any asset, any stream to request a frame, provides the parameters for that frame.
        unsafe delegate RS_ERROR pSetFollower(int isFollower); // Used to mark this node as relying on alternative mechanisms to distribute FrameData. Users must provide correct CameraResponseData to sendFrame, and call rs_beginFollowerFrame at the start of the frame, where awaitFrame would normally be called.
        unsafe delegate RS_ERROR pBeginFollowerFrame(double tTracked); // Pass the engine-distributed tTracked value in, if you have called rs_setFollower(1) otherwise do not call this function.

        unsafe delegate RS_ERROR pGetFrameParameters(UInt64 schemaHash, /*Out*/ /*void**/ IntPtr outParameterData, UInt64 outParameterDataSize);  // returns the remote parameters for this frame.
        unsafe delegate RS_ERROR pGetFrameImageData(UInt64 schemaHash, /*Out*/ /*ImageFrameData**/ IntPtr outParameterData, UInt64 outParameterDataCount);   // returns the remote image data for this frame.
        unsafe delegate RS_ERROR pGetFrameImage(Int64 imageId, /*const SenderFrame**/ IntPtr data); // fills in (data) with the remote image
        unsafe delegate RS_ERROR pGetFrameText(UInt64 schemaHash, UInt32 textParamIndex, /*Out*/ /*const char***/ ref IntPtr outTextPtr); // // returns the remote text data (pointer only valid until next rs_awaitFrameData)

        unsafe delegate RS_ERROR pGetFrameCamera(StreamHandle streamHandle, /*Out*/ /*CameraData**/ IntPtr outCameraData);  // returns the CameraData for this stream, or RS_ERROR_NOTFOUND if no camera data is available for this stream on this frame
        unsafe delegate RS_ERROR pSendFrame(StreamHandle streamHandle, /*SenderFrame**/ IntPtr data, /*const void**/ IntPtr frameData); // publish a frame buffer which was generated from the associated tracking and timing information.

        unsafe delegate RS_ERROR pReleaseImage(/*const SenderFrame**/ IntPtr data);

        unsafe delegate RS_ERROR pLogToD3(string str);
        unsafe delegate RS_ERROR pSendProfilingData(/*ProfilingEntry**/ IntPtr entries, int count);
        unsafe delegate RS_ERROR pSetNewStatusMessage(string msg);

        pRegisterLogFunc m_registerLoggingFunc = null;
        pRegisterLogFunc m_registerErrorLoggingFunc = null;
        pRegisterLogFunc m_registerVerboseLoggingFunc = null;

        pUnregisterLogFunc m_unregisterLoggingFunc = null;
        pUnregisterLogFunc m_unregisterErrorLoggingFunc = null;
        pUnregisterLogFunc m_unregisterVerboseLoggingFunc = null;

        pInitialise m_initialise = null;
        pInitialiseGpGpuWithoutInterop m_initialiseGpGpuWithoutInterop = null;
        pInitialiseGpGpuWithDX11Device m_initialiseGpGpuWithDX11Device = null;
        pInitialiseGpGpuWithDX11Resource m_initialiseGpGpuWithDX11Resource = null;
        pInitialiseGpGpuWithDX12DeviceAndQueue m_initialiseGpGpuWithDX12DeviceAndQueue = null;
        pInitialiseGpGpuWithOpenGlContexts m_initialiseGpGpuWithOpenGlContexts = null;
        pInitialiseGpGpuWithVulkanDevice m_initialiseGpGpuWithVulkanDevice = null;

        pShutdown m_shutdown = null;

        pUseDX12SharedHeapFlag m_useDX12SharedHeapFlag = null;
        pSaveSchema m_saveSchema = null;
        pLoadSchema m_loadSchema = null;

        pSetSchema m_setSchema = null;
        pGetStreams m_getStreams = null;

        pAwaitFrameData m_awaitFrameData = null;
        pSetFollower m_setFollower = null;
        pBeginFollowerFrame m_beginFollowerFrame = null;

        pGetFrameParameters m_getFrameParameters = null;
        pGetFrameImageData m_getFrameImageData = null;
        pGetFrameImage m_getFrameImage = null;
        pGetFrameText m_getFrameText = null;

        pGetFrameCamera m_getFrameCamera = null;
        pSendFrame m_sendFrame = null;

        pReleaseImage m_releaseImage = null;

        pLogToD3 m_logToD3 = null;
        pSendProfilingData m_sendProfilingData = null;
        pSetNewStatusMessage m_setNewStatusMessage = null;

        logger_t m_logInfo;
        logger_t m_logError;

        void logInfo(string message)
        {
            Debug.Log(message);
        }

        void logError(string message)
        {
            Debug.LogError(message);
        }

        void logToD3(string logString, string stackTrace, LogType type)
        {
            if (m_logToD3 == null)
                return;

            string prefix = "";
            switch(type)
            {
                case LogType.Error:
                    prefix = "!!!!! ";
                    break;
                case LogType.Assert:
                    prefix = "!!!!! ASSERT: ";
                    break;
                case LogType.Warning:
                    prefix = "!!! ";
                    break;
                case LogType.Exception:
                    prefix = "!!!!! Exception: ";
                    break;
            }

            string trace = String.IsNullOrEmpty(stackTrace) ? "" : "\nTrace: " + stackTrace;

            m_logToD3(prefix + logString + trace);
        }

        void setNewStatusMessage(string message)
        {
            m_setNewStatusMessage?.Invoke(message);
        }

        ManagedSchema schemaToManagedSchema(Schema cSchema)
        {
            ManagedSchema schema = new ManagedSchema();
            schema.channels = new string[cSchema.channels.nChannels];
            for (int i = 0; i < cSchema.channels.nChannels; ++i)
            {
                IntPtr channelPtr = Marshal.ReadIntPtr(cSchema.channels.channels, i * Marshal.SizeOf(typeof(IntPtr)));
                schema.channels[i] = Marshal.PtrToStringAnsi(channelPtr);
            }
            schema.scenes = new ManagedRemoteParameters[cSchema.scenes.nScenes];
            for (int i = 0; i < cSchema.scenes.nScenes; ++i)
            {
                schema.scenes[i] = new ManagedRemoteParameters();
                ManagedRemoteParameters managedParameters = schema.scenes[i];
                RemoteParameters parameters = (RemoteParameters)Marshal.PtrToStructure(cSchema.scenes.scenes + i * Marshal.SizeOf(typeof(RemoteParameters)), typeof(RemoteParameters));
                managedParameters.name = parameters.name;
                managedParameters.parameters = new ManagedRemoteParameter[parameters.nParameters];
                for (int j = 0; j < parameters.nParameters; ++j)
                {
                    managedParameters.parameters[j] = new ManagedRemoteParameter();
                    ManagedRemoteParameter managedParameter = managedParameters.parameters[j];
                    RemoteParameter parameter = (RemoteParameter)Marshal.PtrToStructure(parameters.parameters + j * Marshal.SizeOf(typeof(RemoteParameter)), typeof(RemoteParameter));
                    managedParameter.group = parameter.group;
                    managedParameter.displayName = parameter.displayName;
                    managedParameter.key = parameter.key;
                    managedParameter.type = parameter.type;
                    if (parameter.type == RemoteParameterType.RS_PARAMETER_NUMBER)
                    {
                        managedParameter.min = parameter.defaults.numerical_min;
                        managedParameter.max = parameter.defaults.numerical_max;
                        managedParameter.step = parameter.defaults.numerical_step;
                        managedParameter.defaultValue = parameter.defaults.numerical_defaultValue;
                    }
                    else if (parameter.type == RemoteParameterType.RS_PARAMETER_TEXT)
                    {
                        managedParameter.defaultValue = Marshal.PtrToStringAnsi(parameter.defaults.text_defaultValue);
                    }
                    managedParameter.options = new string[parameter.nOptions];
                    for (int k = 0; k < parameter.nOptions; ++k)
                    {
                        IntPtr optionPtr = Marshal.ReadIntPtr(parameter.options, k * Marshal.SizeOf(typeof(IntPtr)));
                        managedParameter.options[i] = Marshal.PtrToStringAnsi(optionPtr);
                    }
                    managedParameter.dmxOffset = parameter.dmxOffset;
                    managedParameter.dmxType = parameter.dmxType;
                }
                managedParameters.hash = parameters.hash;
            }
            return schema;
        }

        public RS_ERROR saveSchema(string assetPath, ref ManagedSchema schema)
        {
            if (m_saveSchema == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            List<IntPtr> allocations = new List<IntPtr>();
            try
            {
                Schema cSchema = new Schema();
                cSchema.engineName = "Unity Engine";
                cSchema.engineVersion = Application.unityVersion;
				cSchema.pluginVersion = "RS2.0-Unity-v0";
                cSchema.info = Application.productName;
                cSchema.channels.nChannels = (UInt32)schema.channels.Length;
                cSchema.channels.channels = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(IntPtr)) * (int)cSchema.channels.nChannels);
                allocations.Add(cSchema.channels.channels);
                for (int i = 0; i < cSchema.channels.nChannels; ++i)
                {
                    IntPtr channelPtr = Marshal.StringToHGlobalAnsi(schema.channels[i]);
                    allocations.Add(channelPtr);
                    Marshal.WriteIntPtr(cSchema.channels.channels, i * Marshal.SizeOf(typeof(IntPtr)), channelPtr);
                }

                cSchema.scenes.nScenes = (UInt32)schema.scenes.Length;
                cSchema.scenes.scenes = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RemoteParameters)) * (int)cSchema.scenes.nScenes);
                allocations.Add(cSchema.scenes.scenes);
                for (int i = 0; i < cSchema.scenes.nScenes; ++i)
                {
                    ManagedRemoteParameters managedParameters = schema.scenes[i];
                    RemoteParameters parameters = new RemoteParameters();
                    parameters.name = managedParameters.name;
                    parameters.nParameters = (UInt32)managedParameters.parameters.Length;
                    parameters.parameters = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RemoteParameter)) * (int)parameters.nParameters);
                    allocations.Add(parameters.parameters);
                    for (int j = 0; j < parameters.nParameters; ++j)
                    {
                        ManagedRemoteParameter managedParameter = managedParameters.parameters[j];
                        RemoteParameter parameter = new RemoteParameter();
                        parameter.group = managedParameter.group;
                        parameter.displayName = managedParameter.displayName;
                        parameter.key = managedParameter.key;
                        parameter.type = managedParameter.type;
                        if (parameter.type == RemoteParameterType.RS_PARAMETER_NUMBER)
                        {
                            parameter.defaults.numerical_min = managedParameter.min;
                            parameter.defaults.numerical_max = managedParameter.max;
                            parameter.defaults.numerical_step = managedParameter.step;
                            parameter.defaults.numerical_defaultValue = Convert.ToSingle(managedParameter.defaultValue);
                        }
                        else if (parameter.type == RemoteParameterType.RS_PARAMETER_TEXT)
                        {
                            IntPtr textPtr = Marshal.StringToHGlobalAnsi(Convert.ToString(managedParameter.defaultValue));
                            allocations.Add(textPtr);
                            parameter.defaults.text_defaultValue = textPtr;
                        }
                        parameter.nOptions = (UInt32)managedParameter.options.Length;
                        parameter.options = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(IntPtr)) * (int)parameter.nOptions);
                        allocations.Add(parameter.options);
                        for (int k = 0; k < parameter.nOptions; ++k)
                        {
                            IntPtr optionPtr = Marshal.StringToHGlobalAnsi(managedParameter.options[k]);
                            allocations.Add(optionPtr);
                            Marshal.WriteIntPtr(parameter.options, k * Marshal.SizeOf(typeof(IntPtr)), optionPtr);
                        }
                        parameter.dmxOffset = managedParameter.dmxOffset;
                        parameter.dmxType = managedParameter.dmxType;
                        Marshal.StructureToPtr(parameter, parameters.parameters + j * Marshal.SizeOf(typeof(RemoteParameter)), false);
                    }
                    Marshal.StructureToPtr(parameters, cSchema.scenes.scenes + i * Marshal.SizeOf(typeof(RemoteParameters)), false);
                }

                IntPtr schemaPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Schema)));
                allocations.Add(schemaPtr);
                Marshal.StructureToPtr(cSchema, schemaPtr, false);
                RS_ERROR error = m_saveSchema(assetPath, schemaPtr);
                if (error == RS_ERROR.RS_ERROR_SUCCESS)
                {
                    cSchema = (Schema)Marshal.PtrToStructure(schemaPtr, typeof(Schema));
                    schema = schemaToManagedSchema(cSchema);
                }
                return error;
            }
            finally
            {
                foreach (IntPtr ptr in allocations)
                    Marshal.FreeHGlobal(ptr);
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }

        public RS_ERROR loadSchema(string assetPath, ref ManagedSchema schema)
        {
            if (m_loadSchema == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            IntPtr descMem = IntPtr.Zero;
            UInt32 nBytes = 0;
            m_loadSchema(assetPath, descMem, ref nBytes);

            const int MAX_TRIES = 3;
            int iterations = 0;

            RS_ERROR res = RS_ERROR.RS_ERROR_BUFFER_OVERFLOW;
            try
            {
                do
                {
                    Marshal.FreeHGlobal(descMem);
                    descMem = Marshal.AllocHGlobal((int)nBytes);
                    res = m_loadSchema(assetPath, descMem, ref nBytes);
                    if (res == RS_ERROR.RS_ERROR_SUCCESS)
                    {
                        Schema cSchema = (Schema)Marshal.PtrToStructure(descMem, typeof(Schema));
                        schema = schemaToManagedSchema(cSchema);
                    }

                    ++iterations;
                } while (res == RS_ERROR.RS_ERROR_BUFFER_OVERFLOW && iterations < MAX_TRIES);
            }
            finally
            {
                Marshal.FreeHGlobal(descMem);
            }
            return res;
        }

        public RS_ERROR getStreams(ref StreamDescription[] streams)
        {
            if (m_getStreams == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            IntPtr descMem = IntPtr.Zero;
            UInt32 nBytes = 0;
            m_getStreams(descMem, ref nBytes);

            const int MAX_TRIES = 3;
            int iterations = 0;

            RS_ERROR res = RS_ERROR.RS_ERROR_BUFFER_OVERFLOW;
            try
            {
                do
                {
                    Marshal.FreeHGlobal(descMem);
                    descMem = Marshal.AllocHGlobal((int)nBytes);
                    res = m_getStreams(descMem, ref nBytes);
                    if (res == RS_ERROR.RS_ERROR_SUCCESS)
                    {
                        StreamDescriptions desc = (StreamDescriptions)Marshal.PtrToStructure(descMem, typeof(StreamDescriptions));
                        streams = new StreamDescription[desc.nStreams];
                        for (int i = 0; i < desc.nStreams; ++i)
                        {
                            IntPtr current = desc.streams + i * Marshal.SizeOf(typeof(StreamDescription));
                            streams[i] = (StreamDescription)Marshal.PtrToStructure(current, typeof(StreamDescription));
                        }
                    }

                    ++iterations;
                } while (res == RS_ERROR.RS_ERROR_BUFFER_OVERFLOW && iterations < MAX_TRIES);
            }
            finally
            {
                Marshal.FreeHGlobal(descMem);
            }
            return res;
        }

        public RS_ERROR sendFrame(StreamHandle streamHandle, ref SenderFrame data, ref FrameResponseData frameData)
        {
            if (m_sendFrame == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            if (handleReference.IsAllocated)
                handleReference.Free();
            handleReference = GCHandle.Alloc(frameData, GCHandleType.Pinned);
			
			if (handleReference2.IsAllocated)
                handleReference2.Free();
            handleReference2 = GCHandle.Alloc(data, GCHandleType.Pinned);
            
            try
            {
                RS_ERROR error = m_sendFrame(streamHandle, handleReference2.AddrOfPinnedObject(), handleReference.AddrOfPinnedObject());
                return error;
            }
            finally
            {
                if (handleReference.IsAllocated)
                    handleReference.Free();
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }
		
		public RS_ERROR getFrameImage(Int64 imageId, ref SenderFrame data)
        {
            if (m_getFrameImage == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            if (handleReference.IsAllocated)
                handleReference.Free();
            handleReference = GCHandle.Alloc(data, GCHandleType.Pinned);
			
            try
            {
                RS_ERROR error = m_getFrameImage(imageId, handleReference.AddrOfPinnedObject());
                return error;
            }
            finally
            {
                if (handleReference.IsAllocated)
                    handleReference.Free();
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }

        public RS_ERROR awaitFrameData(int timeoutMs, ref FrameData data)
        {
            if (m_awaitFrameData == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            if (handleReference.IsAllocated)
                handleReference.Free();
            handleReference = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                RS_ERROR error = m_awaitFrameData(timeoutMs, handleReference.AddrOfPinnedObject());
                if (error == RS_ERROR.RS_ERROR_SUCCESS)
                {
                    data = (FrameData)Marshal.PtrToStructure(handleReference.AddrOfPinnedObject(), typeof(FrameData));
                }
                return error;
            }
            finally
            {
                if (handleReference.IsAllocated)
                    handleReference.Free();
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }

        public RS_ERROR setFollower(int isFollower)
        {
            if (m_setFollower == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            try
            {
                RS_ERROR error = m_setFollower(isFollower);
                return error;
            }
            finally
            {
            }
        }

        public RS_ERROR beginFollowerFrame(double tTracked)
        {
            if (m_beginFollowerFrame == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            try
            {
                RS_ERROR error = beginFollowerFrame(tTracked);
                return error;
            }
            finally
            {
            }
        }

        public RS_ERROR getFrameParameters(UInt64 schemaHash, ref float[] outParameterData)
        {
            if (m_getFrameParameters == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            if (handleReference.IsAllocated)
                handleReference.Free();
            handleReference = GCHandle.Alloc(outParameterData, GCHandleType.Pinned);
            try
            {
                RS_ERROR error = m_getFrameParameters(schemaHash, handleReference.AddrOfPinnedObject(), (UInt64)outParameterData.Length * sizeof(float));
                if (error == RS_ERROR.RS_ERROR_SUCCESS)
                {
                    Marshal.Copy(handleReference.AddrOfPinnedObject(), outParameterData, 0, outParameterData.Length);
                }
                return error;
            }
            finally
            {
                if (handleReference.IsAllocated)
                    handleReference.Free();
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }

        public RS_ERROR getFrameImageData(UInt64 schemaHash, ref ImageFrameData[] outParameterData)
        {
            if (m_getFrameImageData == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            if (handleReference.IsAllocated)
                handleReference.Free();
            handleReference = GCHandle.Alloc(outParameterData, GCHandleType.Pinned);
            try
            {
                var size = Marshal.SizeOf(typeof(ImageFrameData));
                RS_ERROR error = m_getFrameImageData(schemaHash, handleReference.AddrOfPinnedObject(), (UInt64)outParameterData.Length);
                if (error == RS_ERROR.RS_ERROR_SUCCESS)
                {
                    for (int i = 0; i < outParameterData.Length; ++i)
                    {
                        IntPtr ptr = new IntPtr(handleReference.AddrOfPinnedObject().ToInt64() + i * size);
                        outParameterData[i] = Marshal.PtrToStructure<ImageFrameData>(ptr);
                    }
                }
                return error;
            }
            finally
            {
                if (handleReference.IsAllocated)
                    handleReference.Free();
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }

        public RS_ERROR getFrameImage(Int64 imageId, ref Texture2D texture)
        {
            if (m_getFrameImage == null)
                return RS_ERROR.RS_NOT_INITIALISED;
			
            try
            {
                SenderFrame data = new SenderFrame();
				data.type = SenderFrameType.RS_FRAMETYPE_DX11_TEXTURE;
                data.dx11_resource = texture.GetNativeTexturePtr();
				RS_ERROR error = PluginEntry.instance.getFrameImage(imageId, ref data);
                return error;
            }
            finally
            {
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }

        public RS_ERROR getFrameText(UInt64 schemaHash, UInt32 textParamIndex, ref string text)
        {
            if (m_getFrameText == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            try
            {
                IntPtr textPtr = IntPtr.Zero;
                RS_ERROR error = m_getFrameText(schemaHash, textParamIndex, ref textPtr);
                if (error == RS_ERROR.RS_ERROR_SUCCESS)
                    text = Marshal.PtrToStringAnsi(textPtr);
                return error;
            }
            finally
            {
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }

        public RS_ERROR getFrameCamera(StreamHandle streamHandle, ref CameraData outCameraData)
        {
            if (m_getFrameCamera == null)
                return RS_ERROR.RS_NOT_INITIALISED;

            if (handleReference.IsAllocated)
                handleReference.Free();
            handleReference = GCHandle.Alloc(outCameraData, GCHandleType.Pinned);
            try
            {
                RS_ERROR error = m_getFrameCamera(streamHandle, handleReference.AddrOfPinnedObject());
                if (error == RS_ERROR.RS_ERROR_SUCCESS)
                {
                    outCameraData = (CameraData)Marshal.PtrToStructure(handleReference.AddrOfPinnedObject(), typeof(CameraData));
                }
                return error;
            }
            finally
            {
                if (handleReference.IsAllocated)
                    handleReference.Free();
            }
            //return RS_ERROR.RS_ERROR_UNSPECIFIED;
        }

#if PLUGIN_AVAILABLE

        [DllImport("kernel32.dll")]
        static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr fileHandle, int flags);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        static extern bool FreeLibrary(IntPtr hModule);

        private void free()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.quitting -= free;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= free;
#else
            Application.quitting -= free;
#endif

            if (functionsLoaded)
            {
                if (m_logToD3 != null)
                    Application.logMessageReceivedThreaded -= logToD3;

                if (m_unregisterErrorLoggingFunc != null)
                    m_unregisterErrorLoggingFunc();
                if (m_unregisterLoggingFunc != null)
                    m_unregisterLoggingFunc();

                RS_ERROR error = m_shutdown();
                if (error != RS_ERROR.RS_ERROR_SUCCESS)
                    Debug.LogError(string.Format("Failed to shutdown: {0}", error));
                functionsLoaded = false;
                Debug.Log("Shut down RenderStream");
            }

            if (d3RenderStreamDLL != IntPtr.Zero)
            {
                FreeLibrary(d3RenderStreamDLL);
                d3RenderStreamDLL = IntPtr.Zero;
                Debug.Log("Unloaded RenderStream");
            }

            if (handleReference.IsAllocated)
                handleReference.Free();
        }

        public bool IsAvailable
        {
            get
            {
                UnityEngine.Rendering.GraphicsDeviceType gapi = UnityEngine.SystemInfo.graphicsDeviceType;
                return functionsLoaded && (gapi == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11);
            }
        }
#else
        private void free() {}
        public bool IsAvailable { get { return false; } }
#endif

        const int LOAD_IGNORE_CODE_AUTHZ_LEVEL = 0x00000010;
        const int LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
        const int LOAD_LIBRARY_SEARCH_APPLICATION_DIR = 0x00000200;
        const int LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;
        const int LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        const string _dllName = "d3renderstream";

        const int RENDER_STREAM_VERSION_MAJOR = 2;
        const int RENDER_STREAM_VERSION_MINOR = 0;

        bool functionsLoaded = false;
        IntPtr d3RenderStreamDLL = IntPtr.Zero;
        GCHandle handleReference; // Everything is run under coroutines with odd lifetimes, so store a reference to GCHandle
		GCHandle handleReference2;

        string name;

        // https://answers.unity.com/questions/16804/retrieving-project-name.html?childToView=478633#answer-478633
        public string GetProjectName()
        {
            string[] s = Application.dataPath.Split('/');
            if (s.Length >= 2)
            {
                string projectName = s[s.Length - 2];
                return projectName;
            }
            return "UNKNOWN UNITY PROJECT";
        }

        private bool LoadFn<T>(ref T fn, string fnName) where T : Delegate
        {
            fn = DelegateBuilder<T>(d3RenderStreamDLL, fnName);
            if (fn == null)
            {
                Debug.LogError(string.Format("Failed load function \"{0}\" from {1}.dll", fnName, _dllName));
                return false;
            }
            return true;
        }

        private PluginEntry()
        {
#if PLUGIN_AVAILABLE
            RegistryKey d3Key = Registry.CurrentUser.OpenSubKey("Software");
            if (d3Key != null)
            {
                d3Key = d3Key.OpenSubKey("d3 Technologies");
                if (d3Key != null)
                {
                    d3Key = d3Key.OpenSubKey("d3 Production Suite");
                }
            }

            if (d3Key == null)
            {
                Debug.LogError(string.Format("Failed to find path to {0}.dll. d3 Not installed?", _dllName));
                return;
            }

            string d3ExePath = d3Key.GetValue("exe path").ToString();
            d3ExePath = d3ExePath.Replace(@"\\", @"\");
            int endSeparator = d3ExePath.LastIndexOf(Path.DirectorySeparatorChar);
            if (endSeparator != d3ExePath.Length - 1)
                d3ExePath = d3ExePath.Substring(0, endSeparator + 1);

            string libPath = d3ExePath + _dllName + ".dll";
            d3RenderStreamDLL = LoadWin32Library(libPath);
            if (d3RenderStreamDLL == IntPtr.Zero)
            {
                Debug.LogError(string.Format("Failed to load {0}.dll from {1}", _dllName, d3ExePath));
                return;
            }
            
            functionsLoaded = true;

            functionsLoaded &= LoadFn(ref m_registerLoggingFunc, "rs_registerLoggingFunc");
            functionsLoaded &= LoadFn(ref m_registerErrorLoggingFunc, "rs_registerErrorLoggingFunc");
            functionsLoaded &= LoadFn(ref m_registerVerboseLoggingFunc, "rs_registerVerboseLoggingFunc");

            functionsLoaded &= LoadFn(ref m_unregisterLoggingFunc, "rs_unregisterLoggingFunc");
            functionsLoaded &= LoadFn(ref m_unregisterErrorLoggingFunc, "rs_unregisterErrorLoggingFunc");
            functionsLoaded &= LoadFn(ref m_unregisterVerboseLoggingFunc, "rs_unregisterVerboseLoggingFunc");

            functionsLoaded &= LoadFn(ref m_initialise, "rs_initialise");
            functionsLoaded &= LoadFn(ref m_initialiseGpGpuWithoutInterop, "rs_initialiseGpGpuWithoutInterop");
            functionsLoaded &= LoadFn(ref m_initialiseGpGpuWithDX11Device, "rs_initialiseGpGpuWithDX11Device");
            functionsLoaded &= LoadFn(ref m_initialiseGpGpuWithDX11Resource, "rs_initialiseGpGpuWithDX11Resource");
            functionsLoaded &= LoadFn(ref m_initialiseGpGpuWithDX12DeviceAndQueue, "rs_initialiseGpGpuWithDX12DeviceAndQueue");
            functionsLoaded &= LoadFn(ref m_initialiseGpGpuWithOpenGlContexts, "rs_initialiseGpGpuWithOpenGlContexts");
            functionsLoaded &= LoadFn(ref m_initialiseGpGpuWithVulkanDevice, "rs_initialiseGpGpuWithVulkanDevice");
            functionsLoaded &= LoadFn(ref m_shutdown, "rs_shutdown");

            functionsLoaded &= LoadFn(ref m_useDX12SharedHeapFlag, "rs_useDX12SharedHeapFlag");
            functionsLoaded &= LoadFn(ref m_saveSchema, "rs_saveSchema");
            functionsLoaded &= LoadFn(ref m_loadSchema, "rs_loadSchema");

            functionsLoaded &= LoadFn(ref m_setSchema, "rs_setSchema");

            functionsLoaded &= LoadFn(ref m_getStreams, "rs_getStreams");

            functionsLoaded &= LoadFn(ref m_awaitFrameData, "rs_awaitFrameData");
            functionsLoaded &= LoadFn(ref m_setFollower, "rs_setFollower");
            functionsLoaded &= LoadFn(ref m_beginFollowerFrame, "rs_beginFollowerFrame");

            functionsLoaded &= LoadFn(ref m_getFrameParameters, "rs_getFrameParameters");
            functionsLoaded &= LoadFn(ref m_getFrameImageData, "rs_getFrameImageData");
            functionsLoaded &= LoadFn(ref m_getFrameImage, "rs_getFrameImage2");
            functionsLoaded &= LoadFn(ref m_getFrameText, "rs_getFrameText");

            functionsLoaded &= LoadFn(ref m_getFrameCamera, "rs_getFrameCamera");
            functionsLoaded &= LoadFn(ref m_sendFrame, "rs_sendFrame2");

            functionsLoaded &= LoadFn(ref m_releaseImage, "rs_releaseImage2");

            functionsLoaded &= LoadFn(ref m_logToD3, "rs_logToD3");
            functionsLoaded &= LoadFn(ref m_sendProfilingData, "rs_sendProfilingData");
            functionsLoaded &= LoadFn(ref m_setNewStatusMessage, "rs_setNewStatusMessage");

            if (!functionsLoaded)
            {
                Debug.LogError(string.Format("One or more functions failed load from {0}.dll", _dllName));
                return;
            }

            // There is an issue with these logging callbacks sometimes throwing inside of the dll which can cause all kinds of problems
            // exception consistentency is questionable, often the same exception can be seen at the same point in time
            // however periodically a minor difference may occur where the exception is not thrown where expected or even at all

            m_logInfo = logInfo;
            m_logError = logError;

            if (m_registerLoggingFunc != null)
                m_registerLoggingFunc(m_logInfo);
            if (m_registerErrorLoggingFunc != null)
                m_registerErrorLoggingFunc(m_logError);

            if (m_logToD3 != null)
                 Application.logMessageReceivedThreaded += logToD3;

            RS_ERROR error = m_initialise(RENDER_STREAM_VERSION_MAJOR, RENDER_STREAM_VERSION_MINOR);
            if (error == RS_ERROR.RS_ERROR_INCOMPATIBLE_VERSION)
                Debug.LogError(string.Format("Unsupported RenderStream library, expected version {0}.{1}", RENDER_STREAM_VERSION_MAJOR, RENDER_STREAM_VERSION_MINOR));
            else if (error != RS_ERROR.RS_ERROR_SUCCESS)
                Debug.LogError(string.Format("Failed to initialise: {0}", error));
            else
            {
                Texture2D texture = new Texture2D(1, 1);
                error = m_initialiseGpGpuWithDX11Resource(texture.GetNativeTexturePtr());
                if (error != RS_ERROR.RS_ERROR_SUCCESS)
                    Debug.LogError(string.Format("Failed to initialise GPU interop: {0}", error));
            }

            Debug.Log("Loaded RenderStream");

#if UNITY_EDITOR
            UnityEditor.EditorApplication.quitting += free;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += free;
#else
            Application.quitting += free;
#endif

            name = GetProjectName();
#else
            Debug.LogError(string.Format("{0}.dll is only available on Windows", _dllName));
#endif
        }

        ~PluginEntry()
        {
            free();
        }

        static IntPtr LoadWin32Library(string dllFilePath)
        {
            System.IntPtr moduleHandle = IntPtr.Zero ;
#if PLUGIN_AVAILABLE
            moduleHandle = LoadLibraryEx(dllFilePath, IntPtr.Zero, LOAD_IGNORE_CODE_AUTHZ_LEVEL | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS);
            if (moduleHandle == IntPtr.Zero)
            {
                // I'm gettin last dll error
                int errorCode = Marshal.GetLastWin32Error();
                Debug.LogError(string.Format("There was an error during dll loading : {0}, error - {1}", dllFilePath, errorCode));
            }
#endif
            return moduleHandle;
        }

        static T DelegateBuilder<T>(IntPtr loadedDLL, string functionName) where T : Delegate
        {
            IntPtr pAddressOfFunctionToCall = IntPtr.Zero;
#if PLUGIN_AVAILABLE
            pAddressOfFunctionToCall = GetProcAddress(loadedDLL, functionName);
            if (pAddressOfFunctionToCall == IntPtr.Zero)
            {
                return null;
            }
#endif
            T functionDelegate = Marshal.GetDelegateForFunctionPointer(pAddressOfFunctionToCall, typeof(T)) as T;
            return functionDelegate;
        }
    }

    public class FrameSender
    {
        struct Frame
        {
            public FrameRegion region;
            public RSPixelFormat fmt;
            public AsyncGPUReadbackRequest readback;
            public CameraResponseData responseData;
        }

        private FrameSender() { }
        public FrameSender(string name, Camera cam)
        {
            m_name = name;
            Cam = cam;

            Debug.Log(string.Format("Creating stream {0}", m_name));
            StreamDescription stream = Array.Find(DisguiseRenderStream.streams, s => s.name == name);
            Debug.Log(string.Format("  Channel {0} at {1}x{2}@{3}", stream.channel, stream.width, stream.height, stream.format));

            m_lastFrameCount = -1;
            m_streamHandle = stream.handle;
            m_width = (int)stream.width;
            m_height = (int)stream.height;

            m_frameRegion = new Rect(stream.clipping.left, stream.clipping.top, stream.clipping.right - stream.clipping.left, stream.clipping.bottom - stream.clipping.top);

            RenderTextureDescriptor desc = new RenderTextureDescriptor(m_width, m_height, PluginEntry.ToRenderTextureFormat(stream.format), 24);
            m_sourceTex = new RenderTexture(desc)
            {
                name = m_name + " Texture"
            };
            Cam.targetTexture = m_sourceTex;
            m_convertedTex = new Texture2D(m_sourceTex.width, m_sourceTex.height, PluginEntry.ToTextureFormat(stream.format), false, false);

            Debug.Log(string.Format("Created stream {0} with handle {1}", m_name, m_streamHandle));
        }

        public bool GetCameraData(ref CameraData cameraData)
        {
            return PluginEntry.instance.getFrameCamera(m_streamHandle, ref cameraData) == RS_ERROR.RS_ERROR_SUCCESS;
        }

        public void SendFrame(Texture2D frame)
        {
            unsafe
            {
                SenderFrame data = new SenderFrame();
				data.type = SenderFrameType.RS_FRAMETYPE_DX11_TEXTURE;
                data.dx11_resource = frame.GetNativeTexturePtr();
                RS_ERROR error = PluginEntry.instance.sendFrame(m_streamHandle, ref data, ref m_responseData);
                if (error != RS_ERROR.RS_ERROR_SUCCESS)
                    Debug.LogError(string.Format("Error sending frame: {0}", error));
            }
        }

        public void SendFrame(FrameData frameData, CameraData cameraData)
        {
            if (m_lastFrameCount == Time.frameCount)
                return;

            m_lastFrameCount = Time.frameCount;

            if (m_convertedTex.width != m_sourceTex.width || m_convertedTex.height != m_sourceTex.height)
                m_convertedTex.Reinitialize(m_sourceTex.width, m_sourceTex.height, m_convertedTex.format, false);

            m_cameraResponseData = new CameraResponseData { tTracked = frameData.tTracked, camera = cameraData };

            if (cameraHandleReference.IsAllocated)
                cameraHandleReference.Free();
            cameraHandleReference = GCHandle.Alloc(m_cameraResponseData, GCHandleType.Pinned);

            m_responseData = new FrameResponseData{ cameraData = cameraHandleReference.AddrOfPinnedObject() };

// Blocks HDRP streams in r18.2
// #if UNITY_PIPELINE_HDRP
//             Volume volume = Cam.GetComponent<Volume>();
//             if (!volume.profile)
//                 Debug.Log("Missing profile");

//             if (!volume.profile.TryGet<DisguiseCameraCaptureAfterPostProcess>(out m_captureAfterPostProcess))
//             {
//                 Debug.Log("Missing captureAfterPostProcess");
//                 m_captureAfterPostProcess = volume.profile.Add<DisguiseCameraCaptureAfterPostProcess>(true);
//             }
//             m_captureAfterPostProcess.width.value = (Int32)m_width;
//             m_captureAfterPostProcess.height.value = (Int32)m_height;
// #else
            RenderTexture unflipped = RenderTexture.GetTemporary(m_sourceTex.width, m_sourceTex.height, 0, m_sourceTex.format);
            Graphics.Blit(m_sourceTex, unflipped, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 1.0f));
            Graphics.ConvertTexture(unflipped, m_convertedTex);
            RenderTexture.ReleaseTemporary(unflipped);

            try
            {
                SendFrame(m_convertedTex);
            }
            finally
            {
                if (cameraHandleReference.IsAllocated)
                    cameraHandleReference.Free();
            }
            
// #endif
        }

        public void DestroyStream()
        {
            m_streamHandle = 0;
        }

        public Camera Cam { get; set; }

        private RenderTexture m_sourceTex;
        private FrameResponseData m_responseData;
        private CameraResponseData m_cameraResponseData;
        private GCHandle cameraHandleReference;

        string m_name;
        Texture2D m_convertedTex;
        int m_lastFrameCount;

        StreamHandle m_streamHandle;
        int m_width;
        int m_height;
        Rect m_frameRegion;
        public Rect subRegion
        {
            get
            {
                return m_frameRegion;
            }
        }       
// Blocks HDRP streams in r18.2 
// #if UNITY_PIPELINE_HDRP
//         private DisguiseCameraCaptureAfterPostProcess m_captureAfterPostProcess;
// #endif
    }

}
