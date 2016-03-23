﻿#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2016 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.Properties;
using ShareX.UploadersLib;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ShareX
{
    public class WorkerTask : IDisposable
    {
        public delegate void TaskEventHandler(WorkerTask task);

        public event TaskEventHandler StatusChanged;
        public event TaskEventHandler UploadStarted;
        public event TaskEventHandler UploadProgressChanged;
        public event TaskEventHandler UploadCompleted;

        public TaskInfo Info { get; private set; }

        public TaskStatus Status { get; private set; }

        public bool IsBusy
        {
            get
            {
                return Status == TaskStatus.InQueue || IsWorking;
            }
        }

        public bool IsWorking
        {
            get
            {
                return Status == TaskStatus.Preparing || Status == TaskStatus.Working || Status == TaskStatus.Stopping;
            }
        }

        public bool StopRequested { get; private set; }
        public bool RequestSettingUpdate { get; private set; }

        public Stream Data { get; private set; }

        private Image tempImage;
        private string tempText;
        private ThreadWorker threadWorker;
        private Uploader uploader;
        private TaskReferenceHelper taskReferenceHelper;

        private static string lastSaveAsFolder;

        #region Constructors

        private WorkerTask(TaskSettings taskSettings)
        {
            Status = TaskStatus.InQueue;
            Info = new TaskInfo(taskSettings);
        }

        public static WorkerTask CreateHistoryTask(RecentTask recentTask)
        {
            WorkerTask task = new WorkerTask(null);
            task.Status = TaskStatus.History;
            task.Info.FilePath = recentTask.FilePath;
            task.Info.FileName = recentTask.FileName;
            task.Info.Result.URL = recentTask.URL;
            task.Info.Result.ThumbnailURL = recentTask.ThumbnailURL;
            task.Info.Result.DeletionURL = recentTask.DeletionURL;
            task.Info.Result.ShortenedURL = recentTask.ShortenedURL;
            task.Info.UploadTime = recentTask.Time.ToLocalTime();

            return task;
        }

        public static WorkerTask CreateDataUploaderTask(EDataType dataType, Stream stream, string fileName, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.DataUpload;
            task.Info.DataType = dataType;
            task.Info.FileName = fileName;
            task.Data = stream;
            return task;
        }

        public static WorkerTask CreateFileUploaderTask(string filePath, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.FilePath = filePath;
            task.Info.DataType = TaskHelpers.FindDataType(task.Info.FilePath, taskSettings);

            if (task.Info.TaskSettings.UploadSettings.FileUploadUseNamePattern)
            {
                string ext = Path.GetExtension(task.Info.FilePath);
                task.Info.FileName = TaskHelpers.GetFilename(task.Info.TaskSettings, ext);
            }

            if (task.Info.TaskSettings.AdvancedSettings.ProcessImagesDuringFileUpload && task.Info.DataType == EDataType.Image)
            {
                task.Info.Job = TaskJob.Job;
                task.tempImage = ImageHelpers.LoadImage(task.Info.FilePath);
            }
            else
            {
                task.Info.Job = TaskJob.FileUpload;

                if (!task.LoadFileStream())
                {
                    return null;
                }
            }

            return task;
        }

        public static WorkerTask CreateImageUploaderTask(Image image, TaskSettings taskSettings, string customFileName = null)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.Job;
            task.Info.DataType = EDataType.Image;

            if (!string.IsNullOrEmpty(customFileName))
            {
                task.Info.FileName = Helpers.AppendExtension(customFileName, "bmp");
            }
            else
            {
                task.Info.FileName = TaskHelpers.GetFilename(taskSettings, "bmp", image);
            }

            task.tempImage = image;
            return task;
        }

        public static WorkerTask CreateTextUploaderTask(string text, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.TextUpload;
            task.Info.DataType = EDataType.Text;
            task.Info.FileName = TaskHelpers.GetFilename(taskSettings, taskSettings.AdvancedSettings.TextFileExtension);
            task.tempText = text;
            return task;
        }

        public static WorkerTask CreateURLShortenerTask(string url, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.ShortenURL;
            task.Info.DataType = EDataType.URL;
            task.Info.FileName = string.Format(Resources.UploadTask_CreateURLShortenerTask_Shorten_URL___0__, taskSettings.URLShortenerDestination.GetLocalizedDescription());
            task.Info.Result.URL = url;
            return task;
        }

        public static WorkerTask CreateShareURLTask(string url, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.ShareURL;
            task.Info.DataType = EDataType.URL;
            task.Info.FileName = string.Format(Resources.UploadTask_CreateShareURLTask_Share_URL___0__, taskSettings.URLSharingServiceDestination.GetLocalizedDescription());
            task.Info.Result.URL = url;
            return task;
        }

        public static WorkerTask CreateFileJobTask(string filePath, TaskSettings taskSettings, string customFileName = null)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.FilePath = filePath;
            task.Info.DataType = TaskHelpers.FindDataType(task.Info.FilePath, taskSettings);

            if (!string.IsNullOrEmpty(customFileName))
            {
                string ext = Path.GetExtension(task.Info.FilePath);
                task.Info.FileName = Helpers.AppendExtension(customFileName, ext);
            }
            else if (task.Info.TaskSettings.UploadSettings.FileUploadUseNamePattern)
            {
                string ext = Path.GetExtension(task.Info.FilePath);
                task.Info.FileName = TaskHelpers.GetFilename(task.Info.TaskSettings, ext);
            }

            task.Info.Job = TaskJob.Job;

            if (task.Info.IsUploadJob && !task.LoadFileStream())
            {
                return null;
            }

            return task;
        }

        public static WorkerTask CreateDownloadUploadTask(string url, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.DownloadUpload;
            task.Info.DataType = TaskHelpers.FindDataType(url, taskSettings);

            string filename = URLHelpers.URLDecode(url, 10);
            filename = URLHelpers.GetFileName(filename);
            filename = Helpers.GetValidFileName(filename);

            if (task.Info.TaskSettings.UploadSettings.FileUploadUseNamePattern)
            {
                string ext = Path.GetExtension(filename);
                filename = TaskHelpers.GetFilename(task.Info.TaskSettings, ext);
            }

            if (string.IsNullOrEmpty(filename))
            {
                return null;
            }

            task.Info.FileName = filename;
            task.Info.Result.URL = url;
            return task;
        }

        #endregion Constructors

        public void Start()
        {
            if (Status == TaskStatus.InQueue && !StopRequested)
            {
                Prepare();
                threadWorker = new ThreadWorker();
                threadWorker.DoWork += ThreadDoWork;
                threadWorker.Completed += ThreadCompleted;
                threadWorker.Start(ApartmentState.STA);
            }
        }

        public void StartSync()
        {
            if (Status == TaskStatus.InQueue && !StopRequested)
            {
                Prepare();
                ThreadDoWork();
                ThreadCompleted();
            }
        }

        private void Prepare()
        {
            Status = TaskStatus.Preparing;

            switch (Info.Job)
            {
                case TaskJob.Job:
                case TaskJob.TextUpload:
                    Info.Status = Resources.UploadTask_Prepare_Preparing;
                    break;
                default:
                    Info.Status = Resources.UploadTask_Prepare_Starting;
                    break;
            }

            TaskbarManager.SetProgressState(Program.MainForm, TaskbarProgressBarStatus.Indeterminate);

            OnStatusChanged();
        }

        public void Stop()
        {
            StopRequested = true;

            switch (Status)
            {
                case TaskStatus.InQueue:
                    OnUploadCompleted();
                    break;
                case TaskStatus.Preparing:
                case TaskStatus.Working:
                    if (uploader != null) uploader.StopUpload();
                    Status = TaskStatus.Stopping;
                    Info.Status = Resources.UploadTask_Stop_Stopping;
                    OnStatusChanged();
                    break;
            }
        }

        private void ThreadDoWork()
        {
            Info.StartTime = DateTime.UtcNow;

            CreateTaskReferenceHelper();

            try
            {
                StopRequested = !DoThreadJob();

                if (!StopRequested)
                {
                    DoUploadJob();
                }
            }
            finally
            {
                Dispose();

                if (Info.Job == TaskJob.Job && Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.DeleteFile) && !string.IsNullOrEmpty(Info.FilePath) && File.Exists(Info.FilePath))
                {
                    File.Delete(Info.FilePath);
                }
            }

            if (!StopRequested && Info.Result != null && Info.Result.IsURLExpected && !Info.Result.IsError)
            {
                if (string.IsNullOrEmpty(Info.Result.URL))
                {
                    Info.Result.Errors.Add(Resources.UploadTask_ThreadDoWork_URL_is_empty_);
                }
                else
                {
                    DoAfterUploadJobs();
                }
            }

            Info.UploadTime = DateTime.UtcNow;
        }

        private void CreateTaskReferenceHelper()
        {
            taskReferenceHelper = new TaskReferenceHelper()
            {
                DataType = Info.DataType,
                OverrideFTP = Info.TaskSettings.OverrideFTP,
                FTPIndex = Info.TaskSettings.FTPIndex,
                OverrideCustomUploader = Info.TaskSettings.OverrideCustomUploader,
                CustomUploaderIndex = Info.TaskSettings.CustomUploaderIndex,
                TextFormat = Info.TaskSettings.AdvancedSettings.TextFormat
            };
        }

        private void DoUploadJob()
        {
            if (Info.IsUploadJob)
            {
                if (Program.Settings.ShowUploadWarning && MessageBox.Show(
                    Resources.UploadTask_DoUploadJob_First_time_upload_warning_text,
                    "ShareX - " + Resources.UploadTask_DoUploadJob_First_time_upload_warning,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                {
                    Program.Settings.ShowUploadWarning = false;
                    Program.DefaultTaskSettings.AfterCaptureJob = Program.DefaultTaskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.UploadImageToHost);
                    RequestSettingUpdate = true;
                    Stop();
                }

                if (Program.Settings.LargeFileSizeWarning > 0)
                {
                    long dataSize = Program.Settings.BinaryUnits ? Program.Settings.LargeFileSizeWarning * 1024 * 1024 : Program.Settings.LargeFileSizeWarning * 1000 * 1000;
                    if (Data != null && Data.Length > dataSize)
                    {
                        using (MyMessageBox msgbox = new MyMessageBox(Resources.UploadTask_DoUploadJob_You_are_attempting_to_upload_a_large_file, "ShareX",
                            MessageBoxButtons.YesNo, Resources.UploadManager_IsUploadConfirmed_Don_t_show_this_message_again_))
                        {
                            msgbox.ShowDialog();
                            if (msgbox.IsChecked) Program.Settings.LargeFileSizeWarning = 0;
                            if (msgbox.DialogResult == DialogResult.No) Stop();
                        }
                    }
                }

                if (!StopRequested)
                {
                    Program.Settings.ShowUploadWarning = false;

                    if (Program.UploadersConfig == null)
                    {
                        Program.UploaderSettingsResetEvent.WaitOne();
                    }

                    Status = TaskStatus.Working;
                    Info.Status = Resources.UploadTask_DoUploadJob_Uploading;

                    TaskbarManager.SetProgressState(Program.MainForm, TaskbarProgressBarStatus.Normal);

                    bool cancelUpload = false;

                    if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.ShowBeforeUploadWindow))
                    {
                        BeforeUploadForm form = new BeforeUploadForm(Info);
                        cancelUpload = form.ShowDialog() != DialogResult.OK;
                    }

                    if (!cancelUpload)
                    {
                        if (threadWorker != null)
                        {
                            threadWorker.InvokeAsync(OnUploadStarted);
                        }
                        else
                        {
                            OnUploadStarted();
                        }

                        bool isError = DoUpload();

                        if (isError && Program.Settings.MaxUploadFailRetry > 0)
                        {
                            DebugHelper.WriteLine("Upload failed. Retrying upload.");

                            for (int retry = 1; isError && retry <= Program.Settings.MaxUploadFailRetry; retry++)
                            {
                                isError = DoUpload(retry);
                            }
                        }
                    }
                    else
                    {
                        Info.Result.IsURLExpected = false;
                    }
                }
            }
            else
            {
                Info.Result.IsURLExpected = false;
            }
        }

        private bool DoUpload(int retry = 0)
        {
            bool isError = false;

            if (retry > 0)
            {
                if (Program.Settings.UseSecondaryUploaders)
                {
                    Info.TaskSettings.ImageDestination = Program.Settings.SecondaryImageUploaders[retry - 1];
                    Info.TaskSettings.ImageFileDestination = Program.Settings.SecondaryFileUploaders[retry - 1];
                    Info.TaskSettings.TextDestination = Program.Settings.SecondaryTextUploaders[retry - 1];
                    Info.TaskSettings.TextFileDestination = Program.Settings.SecondaryFileUploaders[retry - 1];
                    Info.TaskSettings.FileDestination = Program.Settings.SecondaryFileUploaders[retry - 1];
                }
                else
                {
                    Thread.Sleep(1000);
                }
            }

            SSLBypassHelper sslBypassHelper = null;

            try
            {
                if (HelpersOptions.AcceptInvalidSSLCertificates)
                {
                    sslBypassHelper = new SSLBypassHelper();
                }

                switch (Info.UploadDestination)
                {
                    case EDataType.Image:
                        Info.Result = UploadImage(Data, Info.FileName);
                        break;
                    case EDataType.Text:
                        Info.Result = UploadText(Data, Info.FileName);
                        break;
                    case EDataType.File:
                        Info.Result = UploadFile(Data, Info.FileName);
                        break;
                }

                StopRequested |= taskReferenceHelper.StopRequested;
            }
            catch (Exception e)
            {
                if (!StopRequested)
                {
                    DebugHelper.WriteException(e);
                    isError = true;
                    if (Info.Result == null) Info.Result = new UploadResult();
                    Info.Result.Errors.Add(e.ToString());
                }
            }
            finally
            {
                if (sslBypassHelper != null)
                {
                    sslBypassHelper.Dispose();
                }

                if (Info.Result == null) Info.Result = new UploadResult();
                if (uploader != null) Info.Result.Errors.AddRange(uploader.Errors);
                isError |= Info.Result.IsError;
            }

            return isError;
        }

        private bool DoThreadJob()
        {
            if (Info.IsUploadJob && Info.TaskSettings.AdvancedSettings.AutoClearClipboard)
            {
                ClipboardHelpers.Clear();
            }

            if (Info.Job == TaskJob.DownloadUpload)
            {
                if (!DownloadAndUpload())
                {
                    return false;
                }
            }

            if (Info.Job == TaskJob.Job)
            {
                if (!DoAfterCaptureJobs())
                {
                    return false;
                }

                DoFileJobs();
            }
            else if (Info.Job == TaskJob.TextUpload && !string.IsNullOrEmpty(tempText))
            {
                DoTextJobs();
            }
            else if (Info.Job == TaskJob.FileUpload && Info.TaskSettings.AdvancedSettings.UseAfterCaptureTasksDuringFileUpload)
            {
                DoFileJobs();
            }

            if (Info.IsUploadJob && Data != null && Data.CanSeek)
            {
                Data.Position = 0;
            }

            return true;
        }

        private bool DoAfterCaptureJobs()
        {
            if (tempImage == null)
            {
                return true;
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.AddImageEffects))
            {
                tempImage = TaskHelpers.AddImageEffects(tempImage, Info.TaskSettings);

                if (tempImage == null)
                {
                    DebugHelper.WriteLine("Error: Applying image effects resulted empty image.");
                    return false;
                }
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.AnnotateImage))
            {
                tempImage = TaskHelpers.AnnotateImage(tempImage, Info.FileName);

                if (tempImage == null)
                {
                    return false;
                }
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyImageToClipboard))
            {
                ClipboardHelpers.CopyImage(tempImage);
                DebugHelper.WriteLine("Image copied to clipboard.");
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.SendImageToPrinter))
            {
                TaskHelpers.PrintImage(tempImage);
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlagAny(AfterCaptureTasks.SaveImageToFile, AfterCaptureTasks.SaveImageToFileWithDialog, AfterCaptureTasks.UploadImageToHost))
            {
                using (tempImage)
                {
                    ImageData imageData = TaskHelpers.PrepareImage(tempImage, Info.TaskSettings);
                    Data = imageData.ImageStream;
                    Info.FileName = Path.ChangeExtension(Info.FileName, imageData.ImageFormat.GetDescription());

                    if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.SaveImageToFile))
                    {
                        string filePath = TaskHelpers.CheckFilePath(Info.TaskSettings.CaptureFolder, Info.FileName, Info.TaskSettings);

                        if (!string.IsNullOrEmpty(filePath))
                        {
                            Info.FilePath = filePath;
                            imageData.Write(Info.FilePath);
                            DebugHelper.WriteLine("Image saved to file: " + Info.FilePath);
                        }
                    }

                    if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.SaveImageToFileWithDialog))
                    {
                        using (SaveFileDialog sfd = new SaveFileDialog())
                        {
                            bool imageSaved;

                            do
                            {
                                if (string.IsNullOrEmpty(lastSaveAsFolder) || !Directory.Exists(lastSaveAsFolder))
                                {
                                    lastSaveAsFolder = Info.TaskSettings.CaptureFolder;
                                }

                                sfd.InitialDirectory = lastSaveAsFolder;
                                sfd.FileName = Info.FileName;
                                sfd.DefaultExt = Path.GetExtension(Info.FileName).Substring(1);
                                sfd.Filter = string.Format("*{0}|*{0}|All files (*.*)|*.*", Path.GetExtension(Info.FileName));
                                sfd.Title = Resources.UploadTask_DoAfterCaptureJobs_Choose_a_folder_to_save + " " + Path.GetFileName(Info.FileName);

                                if (sfd.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(sfd.FileName))
                                {
                                    Info.FilePath = sfd.FileName;
                                    lastSaveAsFolder = Path.GetDirectoryName(Info.FilePath);
                                    imageSaved = imageData.Write(Info.FilePath);

                                    if (imageSaved)
                                    {
                                        DebugHelper.WriteLine("Image saved to file with dialog: " + Info.FilePath);
                                    }
                                }
                                else
                                {
                                    break;
                                }
                            } while (!imageSaved);
                        }
                    }

                    if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.SaveThumbnailImageToFile))
                    {
                        string thumbnailFilename, thumbnailFolder;

                        if (!string.IsNullOrEmpty(Info.FilePath))
                        {
                            thumbnailFilename = Path.GetFileName(Info.FilePath);
                            thumbnailFolder = Path.GetDirectoryName(Info.FilePath);
                        }
                        else
                        {
                            thumbnailFilename = Info.FileName;
                            thumbnailFolder = Info.TaskSettings.CaptureFolder;
                        }

                        Info.ThumbnailFilePath = TaskHelpers.CreateThumbnail(tempImage, thumbnailFolder, thumbnailFilename, Info.TaskSettings);

                        if (!string.IsNullOrEmpty(Info.ThumbnailFilePath))
                        {
                            DebugHelper.WriteLine("Thumbnail saved to file: " + Info.ThumbnailFilePath);
                        }
                    }
                }
            }

            return true;
        }

        private void DoFileJobs()
        {
            if (!string.IsNullOrEmpty(Info.FilePath) && File.Exists(Info.FilePath))
            {
                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.PerformActions) && Info.TaskSettings.ExternalPrograms != null)
                {
                    var actions = Info.TaskSettings.ExternalPrograms.Where(x => x.IsActive);

                    if (actions.Count() > 0)
                    {
                        if (Data != null)
                        {
                            Data.Dispose();
                        }

                        foreach (ExternalProgram fileAction in actions)
                        {
                            Info.FilePath = fileAction.Run(Info.FilePath);
                        }

                        LoadFileStream();
                    }
                }

                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyFileToClipboard))
                {
                    ClipboardHelpers.CopyFile(Info.FilePath);
                }
                else if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyFilePathToClipboard))
                {
                    ClipboardHelpers.CopyText(Info.FilePath);
                }

                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.ShowInExplorer))
                {
                    Helpers.OpenFolderWithFile(Info.FilePath);
                }
            }
        }

        private void DoTextJobs()
        {
            if (Info.TaskSettings.AdvancedSettings.TextTaskSaveAsFile)
            {
                string filePath = TaskHelpers.CheckFilePath(Info.TaskSettings.CaptureFolder, Info.FileName, Info.TaskSettings);

                if (!string.IsNullOrEmpty(filePath))
                {
                    Info.FilePath = filePath;
                    Helpers.CreateDirectoryFromFilePath(Info.FilePath);
                    File.WriteAllText(Info.FilePath, tempText, Encoding.UTF8);
                    DebugHelper.WriteLine("Text saved to file: " + Info.FilePath);
                }
            }

            byte[] byteArray = Encoding.UTF8.GetBytes(tempText);
            Data = new MemoryStream(byteArray);
        }

        private void DoAfterUploadJobs()
        {
            try
            {
                if (Info.TaskSettings.AdvancedSettings.ResultForceHTTPS)
                {
                    Info.Result.URL = URLHelpers.ForceHTTPS(Info.Result.URL);
                    Info.Result.ThumbnailURL = URLHelpers.ForceHTTPS(Info.Result.ThumbnailURL);
                    Info.Result.DeletionURL = URLHelpers.ForceHTTPS(Info.Result.DeletionURL);
                }

                if (Info.Job != TaskJob.ShareURL && (Info.TaskSettings.AfterUploadJob.HasFlag(AfterUploadTasks.UseURLShortener) || Info.Job == TaskJob.ShortenURL ||
                    (Info.TaskSettings.AdvancedSettings.AutoShortenURLLength > 0 && Info.Result.URL.Length > Info.TaskSettings.AdvancedSettings.AutoShortenURLLength)))
                {
                    UploadResult result = ShortenURL(Info.Result.URL);

                    if (result != null)
                    {
                        Info.Result.ShortenedURL = result.ShortenedURL;
                    }
                }

                if (Info.Job != TaskJob.ShortenURL && (Info.TaskSettings.AfterUploadJob.HasFlag(AfterUploadTasks.ShareURL) || Info.Job == TaskJob.ShareURL))
                {
                    ShareURL(Info.Result.ToString());
                    if (Info.Job == TaskJob.ShareURL) Info.Result.IsURLExpected = false;
                }

                if (Info.TaskSettings.AfterUploadJob.HasFlag(AfterUploadTasks.CopyURLToClipboard))
                {
                    string txt;

                    if (!string.IsNullOrEmpty(Info.TaskSettings.AdvancedSettings.ClipboardContentFormat))
                    {
                        txt = new UploadInfoParser().Parse(Info, Info.TaskSettings.AdvancedSettings.ClipboardContentFormat);
                    }
                    else
                    {
                        txt = Info.Result.ToString();
                    }

                    if (!string.IsNullOrEmpty(txt))
                    {
                        ClipboardHelpers.CopyText(txt);
                    }
                }

                if (Info.TaskSettings.AfterUploadJob.HasFlag(AfterUploadTasks.OpenURL))
                {
                    string result;

                    if (!string.IsNullOrEmpty(Info.TaskSettings.AdvancedSettings.OpenURLFormat))
                    {
                        result = new UploadInfoParser().Parse(Info, Info.TaskSettings.AdvancedSettings.OpenURLFormat);
                    }
                    else
                    {
                        result = Info.Result.ToString();
                    }

                    URLHelpers.OpenURL(result);
                }

                if (Info.TaskSettings.AfterUploadJob.HasFlag(AfterUploadTasks.ShowQRCode))
                {
                    threadWorker.InvokeAsync(() => new QRCodeForm(Info.Result.ToString()).Show());
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                if (Info.Result == null) Info.Result = new UploadResult();
                Info.Result.Errors.Add(e.ToString());
            }
        }

        public UploadResult UploadImage(Stream stream, string fileName)
        {
            ImageUploader imageUploader = UploaderFactory.GetImageUploaderServiceByEnum(Info.TaskSettings.ImageDestination).CreateUploader(Program.UploadersConfig, taskReferenceHelper);

            if (imageUploader != null)
            {
                PrepareUploader(imageUploader);

                return imageUploader.Upload(stream, fileName);
            }

            return null;
        }

        public UploadResult UploadText(Stream stream, string fileName)
        {
            TextUploader textUploader = UploaderFactory.GetTextUploaderServiceByEnum(Info.TaskSettings.TextDestination).CreateUploader(Program.UploadersConfig, taskReferenceHelper);

            if (textUploader != null)
            {
                PrepareUploader(textUploader);

                return textUploader.UploadText(stream, fileName);
            }

            return null;
        }

        public UploadResult UploadFile(Stream stream, string fileName)
        {
            FileDestination fileDestination;

            switch (Info.DataType)
            {
                case EDataType.Image:
                    fileDestination = Info.TaskSettings.ImageFileDestination;
                    break;
                case EDataType.Text:
                    fileDestination = Info.TaskSettings.TextFileDestination;
                    break;
                default:
                case EDataType.File:
                    fileDestination = Info.TaskSettings.FileDestination;
                    break;
            }

            FileUploader fileUploader = UploaderFactory.GetFileUploaderServiceByEnum(fileDestination).CreateUploader(Program.UploadersConfig, taskReferenceHelper);

            if (fileUploader != null)
            {
                PrepareUploader(fileUploader);

                return fileUploader.Upload(stream, fileName);
            }

            return null;
        }

        public UploadResult ShortenURL(string url)
        {
            URLShortener urlShortener = UploaderFactory.GetURLShortenerServiceByEnum(Info.TaskSettings.URLShortenerDestination).CreateShortener(Program.UploadersConfig, taskReferenceHelper);

            if (urlShortener != null)
            {
                return urlShortener.ShortenURL(url);
            }

            return null;
        }

        public void ShareURL(string url)
        {
            if (!string.IsNullOrEmpty(url))
            {
                UploaderFactory.GetSharingServiceByEnum(Info.TaskSettings.URLSharingServiceDestination).ShareURL(url, Program.UploadersConfig);
            }
        }

        private void PrepareUploader(Uploader currentUploader)
        {
            uploader = currentUploader;
            uploader.BufferSize = (int)Math.Pow(2, Program.Settings.BufferSizePower) * 1024;
            uploader.ProgressChanged += uploader_ProgressChanged;

            if (Info.TaskSettings.AfterUploadJob.HasFlag(AfterUploadTasks.CopyURLToClipboard) && Info.TaskSettings.AdvancedSettings.EarlyCopyURL)
            {
                uploader.EarlyURLCopyRequested += url => ClipboardHelpers.CopyText(url);
            }
        }

        private bool DownloadAndUpload()
        {
            string url = Info.Result.URL.Trim();
            Info.Result.URL = string.Empty;
            Info.FilePath = TaskHelpers.CheckFilePath(Info.TaskSettings.CaptureFolder, Info.FileName, Info.TaskSettings);

            if (!string.IsNullOrEmpty(Info.FilePath))
            {
                Info.Status = Resources.UploadTask_DownloadAndUpload_Downloading;
                OnStatusChanged();

                try
                {
                    Helpers.CreateDirectoryFromFilePath(Info.FilePath);

                    using (WebClient wc = new WebClient())
                    {
                        wc.Proxy = HelpersOptions.CurrentProxy.GetWebProxy();
                        wc.DownloadFile(url, Info.FilePath);
                    }

                    LoadFileStream();

                    return true;
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                    MessageBox.Show(string.Format(Resources.UploadManager_DownloadAndUploadFile_Download_failed, e), "ShareX", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            return false;
        }

        private bool LoadFileStream()
        {
            try
            {
                Data = new FileStream(Info.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "ShareX - " + Resources.TaskManager_task_UploadCompleted_Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private void ThreadCompleted()
        {
            OnUploadCompleted();
        }

        private void uploader_ProgressChanged(ProgressManager progress)
        {
            if (progress != null)
            {
                Info.Progress = progress;

                if (threadWorker != null)
                {
                    threadWorker.InvokeAsync(OnUploadProgressChanged);
                }
                else
                {
                    OnUploadProgressChanged();
                }
            }
        }

        private void OnStatusChanged()
        {
            if (StatusChanged != null)
            {
                if (threadWorker != null)
                {
                    threadWorker.InvokeAsync(() => StatusChanged(this));
                }
                else
                {
                    StatusChanged(this);
                }
            }
        }

        private void OnUploadStarted()
        {
            if (UploadStarted != null)
            {
                UploadStarted(this);
            }
        }

        private void OnUploadProgressChanged()
        {
            if (UploadProgressChanged != null)
            {
                UploadProgressChanged(this);
            }
        }

        private void OnUploadCompleted()
        {
            Status = TaskStatus.Completed;

            if (StopRequested)
            {
                Info.Status = Resources.UploadTask_OnUploadCompleted_Stopped;
            }
            else
            {
                Info.Status = Resources.UploadTask_OnUploadCompleted_Done;
            }

            if (UploadCompleted != null)
            {
                UploadCompleted(this);
            }

            Dispose();
        }

        public void Dispose()
        {
            if (Data != null)
            {
                Data.Dispose();
                Data = null;
            }

            if (tempImage != null)
            {
                tempImage.Dispose();
                tempImage = null;
            }
        }
    }
}