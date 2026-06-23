using MfgInspectionSystem.Communication.Messages;
using OpenCvSharp;
using Serilog;

namespace MfgInspectionSystem.Vision;

public class OpenCvPostResult
{
    public int PinCount { get; set; }
    public double BlurScore { get; set; }
    public bool RoiAligned { get; set; }
}

public class OpenCvPostProcessor
{
    public OpenCvPostResult Process(byte[] imageBytes, YoloDetection det)
    {
        var result = new OpenCvPostResult { RoiAligned = true };
        try
        {
            using var mat = Mat.FromImageData(imageBytes, ImreadModes.Color);
            if (mat.Empty()) return result;

            int W = mat.Width;
            int H = mat.Height;

            // Step 1: ROI with 10% padding (v2 spec)
            const double pad = 0.10;
            int x1 = (int)Math.Max(0, det.X1 - det.W * pad);
            int y1 = (int)Math.Max(0, det.Y1 - det.H * pad);
            int x2 = (int)Math.Min(W - 1, det.X2 + det.W * pad);
            int y2 = (int)Math.Min(H - 1, det.Y2 + det.H * pad);

            if (x2 <= x1 || y2 <= y1)
            {
                result.BlurScore = 999;
                return result;
            }

            using var roi = new Mat(mat, new Rect(x1, y1, x2 - x1, y2 - y1));

            // Step 2: Blur (Laplacian variance)
            using var gray = new Mat();
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
            using var lap = new Mat();
            Cv2.Laplacian(gray, lap, MatType.CV_64F);
            Cv2.MeanStdDev(lap, out _, out var stddev);
            result.BlurScore = stddev.Val0 * stddev.Val0;

            // Step 3: Pin count (v2 spec)
            result.PinCount = CountPinsV2(gray);

            // Step 4: ROI alignment (v2 spec: 4-edge margin check, not center-offset)
            const int margin = 20;
            result.RoiAligned = x1 >= margin && y1 >= margin
                                && x2 <= W - margin && y2 <= H - margin;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OpenCV post-processing failed");
        }
        return result;
    }

    // ── YOLO 전처리 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// YOLO 추론 전 이미지 전처리.
    /// - LAB 색공간에서 L채널만 CLAHE → 색상 왜곡 없이 대비 향상
    /// - 어둡거나 노이즈 심한 환경에서 bbox 오탐/저신뢰도 개선 목적
    /// - 실패 시 원본 bytes 반환 (안전 fallback)
    /// 효과 없으면 appsettings.json Vision.EnableYoloPreprocessing: false 로 끄기.
    /// </summary>
    public static byte[] PreprocessForYolo(byte[] imageBytes)
    {
        try
        {
            using var src = Mat.FromImageData(imageBytes, ImreadModes.Color);
            if (src.Empty()) return imageBytes;

            // BGR → LAB (L=밝기, A/B=색상 분리)
            using var lab = new Mat();
            Cv2.CvtColor(src, lab, ColorConversionCodes.BGR2Lab);

            // L 채널에만 CLAHE 적용 (clipLimit 2.0, 8×8 타일)
            // clipLimit 낮을수록 보수적, 높을수록 강하게 향상
            Mat[] channels = Cv2.Split(lab);            try
            {
                using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));
                using var lEnhanced = new Mat();
                clahe.Apply(channels[0], lEnhanced);

                using var labOut = new Mat();
                Cv2.Merge(new Mat[] { lEnhanced, channels[1], channels[2] }, labOut);

                using var result = new Mat();
                Cv2.CvtColor(labOut, result, ColorConversionCodes.Lab2BGR);

                Cv2.ImEncode(".jpg", result, out var buf,
                    new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));

                Log.Debug("YOLO preprocess OK: {Before}b → {After}b", imageBytes.Length, buf.Length);
                return buf;
            }
            finally
            {
                foreach (var ch in channels) ch.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "YOLO preprocess failed — 원본 이미지 그대로 사용");
            return imageBytes;
        }
    }

    /// <summary>
    /// Pin counting per D spec v2.
    /// Region: bottom 35% of ROI (y >= 65%).
    /// Threshold: BinaryInv + Otsu (dark pins → white contours).
    /// Morphology: Opening 3x3 to remove noise.
    /// Area filter: max(20, totalROI_area * 0.001).
    /// </summary>
    private static int CountPinsV2(Mat gray)
    {
        try
        {
            int totalH = gray.Height;
            int totalW = gray.Width;
            int totalArea = totalH * totalW;

            // Bottom 35% of the ROI
            int pinRegionY = (int)(totalH * 0.65);
            if (pinRegionY >= totalH) return 0;

            using var pinRegion = new Mat(gray,
                new Rect(0, pinRegionY, totalW, totalH - pinRegionY));

            // BinaryInv + Otsu: dark pins become white in the result
            using var binary = new Mat();
            Cv2.Threshold(pinRegion, binary, 0, 255,
                ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

            // Morphological opening — removes single-pixel noise before contour search
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel, iterations: 1);

            Cv2.FindContours(binary, out var contours, out _,
                RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            // Area threshold is based on full ROI area, not pin-region area
            double minArea = Math.Max(20, totalArea * 0.001);
            return contours.Count(c => Cv2.ContourArea(c) >= minArea);
        }
        catch
        {
            return 0;
        }
    }
}
