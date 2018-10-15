﻿
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Java.Lang;
using Java.Util.Concurrent;

namespace SupportMediaXF.Droid.SupportMediaExtended.Camera
{
    [Activity(Label = "CamActivity")]
    public class CamActivity : Activity, TextureView.ISurfaceTextureListener
    {
        private ImageButton bttCapture, bttBack, bttSwitch, bttFlash;
        private ProgressBar progressBar;

        private static readonly SparseIntArray ORIENTATIONS = new SparseIntArray();
        private CameraStateListener cameraStateListener;
        private CaptureRequest.Builder captureRequestBuilder;
        private CameraCaptureSession cameraCaptureSession;
        private SurfaceTexture surfaceTexture;
        private AutoFitTextureView autoFitTextureView;
        private Size previewSize;
        private CameraManager cameraManager;
        public CameraDevice cameraDevice;

        private bool flashOn = false;

        public Semaphore mCameraOpenCloseLock = new Semaphore(1);
        public Semaphore SwitchCameraLock = new Semaphore(1);


        private bool mFlashSupported;
        private int CurrentCameraIndex = -1;
        private byte[] Photo;

        private bool IsBusy = false;

        private void ShowToastMessage(string message)
        {
            Toast.MakeText(this, message, ToastLength.Short).Show();
        }

        private void DebugMessage(string content)
        {
            System.Diagnostics.Debug.WriteLine(content);
        }

        public void ConfigureTransform(int viewWidth, int viewHeight)
        {
            if (surfaceTexture != null && previewSize != null)
            {
                var windowManager = GetSystemService(Context.WindowService).JavaCast<IWindowManager>();

                var rotation = windowManager.DefaultDisplay.Rotation;
                var matrix = new Matrix();
                var viewRect = new RectF(0, 0, viewWidth, viewHeight);
                var bufferRect = new RectF(0, 0, previewSize.Width, previewSize.Height);

                var centerX = viewRect.CenterX();
                var centerY = viewRect.CenterY();

                if (rotation == SurfaceOrientation.Rotation90 || rotation == SurfaceOrientation.Rotation270)
                {
                    bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());
                    matrix.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);

                    var scale = System.Math.Max((float)viewHeight / previewSize.Height, (float)viewWidth / previewSize.Width);
                    matrix.PostScale(scale, scale, centerX, centerY);
                    matrix.PostRotate(90 * ((int)rotation - 2), centerX, centerY);
                }

                autoFitTextureView.SetTransform(matrix);
            }
        }

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            surfaceTexture = surface;

            ConfigureTransform(width, height);
            StartPreview();
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            if(cameraCaptureSession != null)
                cameraCaptureSession.StopRepeating();

            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
        {
            ConfigureTransform(width, height);
            StartPreview();
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface)
        {
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
			SetContentView(Resource.Layout.cam_activity);
           
            if (ActionBar != null)
                ActionBar.Hide();


            autoFitTextureView = FindViewById<AutoFitTextureView>(Resource.Id.CameraTexture);
            bttBack = FindViewById<ImageButton>(Resource.Id.bttBack);
            bttFlash = FindViewById<ImageButton>(Resource.Id.bttFlash);
            bttSwitch = FindViewById<ImageButton>(Resource.Id.bttSwitchCamera);
            bttCapture = FindViewById<ImageButton>(Resource.Id.bttCapture);
            progressBar = FindViewById<ProgressBar>(Resource.Id.progressBar_cyclic);

            bttBack.Click += (object sender, EventArgs e) => {
                Finish();
            };

            bttFlash.Click += (object sender, EventArgs e) => {
                try
                {
                    flashOn = !flashOn;
                    captureRequestBuilder.Set(CaptureRequest.FlashMode, new Integer(flashOn ? (int)FlashMode.Torch : (int)FlashMode.Off));
                    UpdatePreview();
                }
                catch (System.Exception error)
                {
                    ShowToastMessage("Failed to switch flash on/off");
                    DebugMessage("ErrorMessage: \n" + error.Message + "\n" + "Stacktrace: \n " + error.StackTrace);
                }
            };

            bttSwitch.Click += (object sender, EventArgs e) => {
                 SwitchCamera();
            };

            bttCapture.Click += (object sender, EventArgs e) => {
                TakePhoto();
            };

            autoFitTextureView.SurfaceTextureListener = this;
            cameraStateListener = new CameraStateListener() { Camera = this };

            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation0, 90);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation90, 0);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation180, 270);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation270, 180);

            OpenCamera(false);

            //Task.Delay(500).ContinueWith((arg) =>
            //{
            //    RunOnUiThread(() =>
            //    {

            //    });
            //});

        }

        private void SwitchCamera()
        {
            try
            {
                SwitchCameraLock.Acquire();

                progressBar.Visibility = ViewStates.Visible;

                CloseCamera();
                OpenCamera(true);

                progressBar.Visibility = ViewStates.Gone;
            }
            catch (System.Exception ex)
            {
                ShowToastMessage("Failed to switch camera");
                DebugMessage("ErrorMessage: \n" + ex.Message + "\n" + "Stacktrace: \n " + ex.StackTrace);
            }
            finally
            {
                SwitchCameraLock.Release();
            }
        }

        private void CloseCamera()
        {
            try
            {
                mCameraOpenCloseLock.Acquire();
                if (null != cameraCaptureSession)
                {
                    cameraCaptureSession.Close();
                    cameraCaptureSession = null;
                }
                if (null != cameraDevice)
                {
                    cameraDevice.Close();
                    cameraDevice = null;
                }
            }
            catch (InterruptedException e)
            {
                ShowToastMessage("Failed to close camera");
                DebugMessage("ErrorMessage: \n" + e.Message + "\n" + "Stacktrace: \n " + e.StackTrace);
            }
            finally
            {
                mCameraOpenCloseLock.Release();
            }
        }

        public void StartPreview()
        {
            if (cameraDevice != null && autoFitTextureView.IsAvailable && previewSize != null)
            {
                try
                {
                    var texture = autoFitTextureView.SurfaceTexture;
                    System.Diagnostics.Debug.Assert(texture != null);
                    texture.SetDefaultBufferSize(previewSize.Width, previewSize.Height);

                    var surface = new Surface(texture);

                    captureRequestBuilder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    captureRequestBuilder.AddTarget(surface);

                    cameraDevice.CreateCaptureSession(new List<Surface>() { surface },
                        new CameraCaptureStateListener()
                        {
                            OnConfigureFailedAction = (CameraCaptureSession session) =>
                            {
                            },
                            OnConfiguredAction = (CameraCaptureSession session) =>
                            {
                                cameraCaptureSession = session;
                                UpdatePreview();
                            }
                        },
                        null);
                }
                catch (Java.Lang.Exception error)
                {
                    ShowToastMessage("Failed to start preview");
                    DebugMessage("ErrorMessage: \n" + error.Message + "\n" + "Stacktrace: \n " + error.StackTrace);
                }
            }
        }

        private void SyncCameraPosition(bool IsSwitchCamere)
        {
            if(cameraManager != null)
            {
                var listCam = cameraManager.GetCameraIdList();

                if (CurrentCameraIndex == -1)
                {
                    if (listCam.Length > 0)
                        CurrentCameraIndex = 0;
                }

                if (IsSwitchCamere)
                {
                    var nextCam = CurrentCameraIndex + 1;

                    if (nextCam <= listCam.Length - 1)
                    {
                        CurrentCameraIndex = nextCam;
                    }
                    else
                    {
                        if (listCam.Length > 0)
                            CurrentCameraIndex = 0;
                    }
                }
            }
        }

        public void OpenCamera(bool IsSwitchCam)
        {

            cameraManager = (CameraManager)GetSystemService(Context.CameraService);
            try
            {

                SyncCameraPosition(IsSwitchCam);
                string cameraId = cameraManager.GetCameraIdList()[CurrentCameraIndex];

                CameraCharacteristics characteristics = cameraManager.GetCameraCharacteristics(cameraId);
                StreamConfigurationMap map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                previewSize = map.GetOutputSizes(Java.Lang.Class.FromType(typeof(SurfaceTexture)))[0];
                Android.Content.Res.Orientation orientation = Resources.Configuration.Orientation;
                if (orientation == Android.Content.Res.Orientation.Landscape)
                {
                    autoFitTextureView.SetAspectRatio(previewSize.Width, previewSize.Height);
                }
                else
                {
                    autoFitTextureView.SetAspectRatio(previewSize.Height, previewSize.Width);
                }

                HandlerThread thread = new HandlerThread("CameraPreview");
                thread.Start();
                Handler backgroundHandler = new Handler(thread.Looper);

                cameraManager.OpenCamera(cameraId, cameraStateListener, null);

                var available = (bool)characteristics.Get(CameraCharacteristics.FlashInfoAvailable);
                if (available)
                {
                    mFlashSupported = false;
                }
                else
                {
                    mFlashSupported = (bool)available;
                }

            }
            catch (Java.Lang.Exception error)
            {
                ShowToastMessage("Failed to open camera");
                DebugMessage("ErrorMessage: \n" + error.Message + "\n" + "Stacktrace: \n " + error.StackTrace);
            }
            catch (System.Exception error)
            {
                ShowToastMessage("Failed to open camera");
                DebugMessage("ErrorMessage: \n" + error.Message + "\n" + "Stacktrace: \n " + error.StackTrace);
            }
        }

        private void UpdatePreview()
        {
            if (cameraDevice != null && cameraCaptureSession != null)
            {
                try
                {
                    captureRequestBuilder.Set(CaptureRequest.ControlMode, new Java.Lang.Integer((int)ControlMode.Auto));

                    HandlerThread thread = new HandlerThread("CameraPicture");
                    thread.Start();
                    Handler backgroundHandler = new Handler(thread.Looper);

                    cameraCaptureSession.SetRepeatingRequest(captureRequestBuilder.Build(), null, backgroundHandler);
                }
                catch (CameraAccessException error)
                {
                    ShowToastMessage("Failed to access camera");
                    DebugMessage("ErrorMessage: \n" + error.Message + "\n" + "Stacktrace: \n " + error.StackTrace);
                }
                catch (IllegalStateException error)
                {
                    ShowToastMessage("Failed to access camera");
                    DebugMessage("ErrorMessage: \n" + error.Message + "\n" + "Stacktrace: \n " + error.StackTrace);
                }
            }
        }



        public void TakePhoto()
        {

            if (cameraDevice != null)
            {
                try
                {

                    // Pick the best JPEG size that can be captures with this CameraDevice
                    var characteristics = cameraManager.GetCameraCharacteristics(cameraDevice.Id);
                    Android.Util.Size[] jpegSizes = null;
                    if (characteristics != null)
                    {
                        jpegSizes = ((StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap)).GetOutputSizes((int)ImageFormatType.Jpeg);
                    }
                    int width = 640;
                    int height = 480;

                    if (jpegSizes != null && jpegSizes.Length > 0)
                    {
                        width = jpegSizes[0].Width;
                        height = jpegSizes[0].Height;
                    }

                    // We use an ImageReader to get a JPEG from CameraDevice
                    // Here, we create a new ImageReader and prepare its Surface as an output from the camera
                    var reader = ImageReader.NewInstance(width, height, ImageFormatType.Jpeg, 1);
                    var outputSurfaces = new List<Surface>(2);
                    outputSurfaces.Add(reader.Surface);
                    outputSurfaces.Add(new Surface(surfaceTexture));

                    CaptureRequest.Builder captureBuilder = cameraDevice.CreateCaptureRequest(CameraTemplate.StillCapture);
                    captureBuilder.AddTarget(reader.Surface);
                    captureBuilder.Set(CaptureRequest.ControlMode, new Integer((int)ControlMode.Auto));

                    // Orientation
                    var windowManager = GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
                    SurfaceOrientation rotation = windowManager.DefaultDisplay.Rotation;

                    captureBuilder.Set(CaptureRequest.JpegOrientation, new Integer(ORIENTATIONS.Get((int)rotation)));

                    // This listener is called when an image is ready in ImageReader 
                    ImageAvailableListener readerListener = new ImageAvailableListener();

                    readerListener.Photo += (sender, e) =>
                    {
                        Photo = e;
                    };

                    // We create a Handler since we want to handle the resulting JPEG in a background thread
                    HandlerThread thread = new HandlerThread("CameraPicture");
                    thread.Start();
                    Handler backgroundHandler = new Handler(thread.Looper);
                    reader.SetOnImageAvailableListener(readerListener, backgroundHandler);

                    var captureListener = new CameraCaptureListener();

                    captureListener.PhotoComplete += (sender, e) =>
                    {
                        StartPreview();
                    };

                    cameraDevice.CreateCaptureSession(outputSurfaces, new CameraCaptureStateListener()
                    {
                        OnConfiguredAction = (CameraCaptureSession session) =>
                        {
                            try
                            {
                                cameraCaptureSession = session;
                                session.Capture(captureBuilder.Build(), captureListener, backgroundHandler);
                            }
                            catch (CameraAccessException ex)
                            {
                                Log.WriteLine(LogPriority.Info, "Capture Session error: ", ex.ToString());
                            }
                        }
                    }, backgroundHandler);
                }
                catch (CameraAccessException error)
                {
                    ShowToastMessage("Failed to take photo");
                    DebugMessage("ErrorMessage: \n" + error.Message + "\n" + "Stacktrace: \n " + error.StackTrace);
                }
                catch (Java.Lang.Exception error)
                {
                    ShowToastMessage("Failed to take photo");
                    DebugMessage("ErrorMessage: \n" + error.Message + "\n" + "Stacktrace: \n " + error.StackTrace);
                }
            }
        }
    }
}
