/*

The results of ray tracing calculations (like color, intensity, etc.) are rendered to a render texture. 
This texture acts as a canvas that captures the final image as computed by the ray tracing process. 
Finally, the render texture is displayed on the screen or used for further processing.

*/

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PathTracingMaster : MonoBehaviour
{
    public ComputeShader PathTracingShader;
    public Texture SkyboxTexture;
    public Light DirectionalLight;

    [Header("Spheres")]
    public int SphereSeed;
    public Vector2 SphereRadius = new Vector2(3.0f, 8.0f);
    public uint SpheresMax = 100;
    public float SpherePlacementRadius = 100.0f;

    private Camera _camera;
    private float _lastFieldOfView;

    private RenderTexture _target;
    private RenderTexture _converged;

    private uint _currentSample = 0;
    private Material _addMaterial;
    private ComputeBuffer _sphereBuffer;
    private List<Transform> _transformsToWatch = new List<Transform>();

    struct Sphere
    {
        public Vector3 position;
        public float radius;
        public Vector3 albedo;
        public Vector3 specular;
        public float smoothness;
        public Vector3 emission;
    }

    private void Awake()
    {
        _camera = GetComponent<Camera>();
        _transformsToWatch.Add(transform);
        _transformsToWatch.Add(DirectionalLight.transform);
    }

    private void OnEnable()
    {
        _currentSample = 0;
        SetUpScene();
    }

    private void OnDisable()
    {
        if (_sphereBuffer != null)
            _sphereBuffer.Release();
    }

    void Start()
    {
        //Debug.Log("screen height " + Screen.height + " screen width " + Screen.width);
    }

    void Update()
    {
        //VisualizeRays();

        if (_camera.fieldOfView != _lastFieldOfView)
        {
            _currentSample = 0;
            _lastFieldOfView = _camera.fieldOfView;
        }

        foreach (Transform t in _transformsToWatch)
        {
            if (t.hasChanged)
            {
                _currentSample = 0;
                t.hasChanged = false;
            }
        }
    }

    private void VisualizeRays()
    {
        int width = Screen.width;
        int height = Screen.height;

        for (int x = 0; x < width; x += 100) // Increment by 10 for performance
        {
            for (int y = 0; y < height; y += 100)
            {
                Vector2 uv = new Vector2((float)x / width, (float)y / height);
                Ray ray = _camera.ViewportPointToRay(uv);

                // Visualize the ray (set distance and color as needed)
                Debug.DrawRay(ray.origin, ray.direction * 100, Color.red);
            }
        }
    }

    private void SetUpScene()
    {
        UnityEngine.Random.InitState(SphereSeed);
        List<Sphere> spheres = new List<Sphere>();

        // Add a number of random spheres
        for (int i = 0; i < SpheresMax; i++)
        {
            Sphere sphere = new Sphere();

            // Radius
            sphere.radius = SphereRadius.x + UnityEngine.Random.value * (SphereRadius.y - SphereRadius.x);
            Vector2 randomPos = UnityEngine.Random.insideUnitCircle * SpherePlacementRadius;
            sphere.position = new Vector3(randomPos.x, sphere.radius, randomPos.y);

            // Reject spheres that are intersecting others
            foreach (Sphere other in spheres)
            {
                float minDist = sphere.radius + other.radius;
                if (Vector3.SqrMagnitude(sphere.position - other.position) < minDist * minDist)
                    goto SkipSphere;
            }

            // Albedo and specular color
            Color color = UnityEngine.Random.ColorHSV();
            float chance = UnityEngine.Random.value;
            if (chance < 0.8f)
            {
                bool metal = chance < 0.4f;
                sphere.albedo = metal ? new Vector4(0.1f, 0.1f, 0.1f) : new Vector4(color.r, color.g, color.b);
                sphere.specular = metal ? new Vector4(color.r, color.g, color.b) : new Vector4(0.1f, 0.1f, 0.1f);
                sphere.smoothness = UnityEngine.Random.value; // Shininess property
            }
            else
            {
                Color emission = UnityEngine.Random.ColorHSV(0, 1, 0, 1, 3.0f, 8.0f);
                sphere.emission = new Vector3(emission.r, emission.g, emission.b); // Glowing property
            }

            // Add the sphere to the list
            spheres.Add(sphere);

        SkipSphere:
            continue;
        }

        // Assign to compute buffer
        if (_sphereBuffer != null)
            _sphereBuffer.Release();
        if (spheres.Count > 0)
        {
            _sphereBuffer = new ComputeBuffer(spheres.Count, 56);
            _sphereBuffer.SetData(spheres);
        }
    }

    private void SetShaderParameters()
    {
        PathTracingShader.SetFloat("_Seed", UnityEngine.Random.value);
        PathTracingShader.SetTexture(0, "_SkyboxTexture", SkyboxTexture);

        PathTracingShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        PathTracingShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse); // 2D->3D        
        PathTracingShader.SetVector("_PixelOffset", new Vector2(UnityEngine.Random.value, UnityEngine.Random.value));

        Vector3 light = DirectionalLight.transform.forward;
        PathTracingShader.SetVector("_DirectionalLight", new Vector4(light.x, light.y, light.z, DirectionalLight.intensity));

        if (_sphereBuffer != null)
            PathTracingShader.SetBuffer(0, "_Spheres", _sphereBuffer);
    }


    // When the screen size changes (like resizing the game window), 
    // InitRenderTexture() ensures that the textures used for rendering the scene are updated to match the new size.    
    private void InitRenderTexture()
    {
        if (_target == null || _target.width != Screen.width || _target.height != Screen.height)
        {
            // Release render texture if we already have one
            if (_target != null)
            {
                _target.Release();
                _converged.Release();
            }

            // Get a render target for Ray Tracing
            _target = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.enableRandomWrite = true;
            _target.Create();

            _converged = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _converged.enableRandomWrite = true;
            _converged.Create();

            // Reset sampling
            _currentSample = 0;
        }
    }

    //This process progressively renders the scene using path tracing, 
    //refining the result with each frame by accumulating the results, which reduces noise and improves image quality.
    private void Render(RenderTexture destination)
    {
        // Make sure we have a current render target
        InitRenderTexture();

        // Set the target and dispatch the compute shader
        PathTracingShader.SetTexture(0, "Result", _target);
        int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);
        PathTracingShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        if (_addMaterial == null)
            _addMaterial = new Material(Shader.Find("Hidden/AddShader"));
        _addMaterial.SetFloat("_Sample", _currentSample);

        Graphics.Blit(_target, _converged, _addMaterial); // The shader accumulates the rendering result from _target into _converged, smoothing out noise over multiple samples.
        Graphics.Blit(_converged, destination); // The accumulated result in _converged is then drawn to the destination render texture, typically the screen.    

        _currentSample++;
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        SetShaderParameters();
        Render(destination);
    }
}