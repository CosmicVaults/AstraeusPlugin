using NINA.Astrometry;
using NINA.PlateSolving;
using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    public readonly record struct FrameWcs(
        double CrVal1, double CrVal2,
        double CrPix1, double CrPix2,
        double Cd11, double Cd12, double Cd21, double Cd22) {

        /// <summary>
        /// Uses the full CD matrix, since the CDELT/CROTA2 form assumes an unmirrored frame and gives wrong
        /// position angles on flipped images.
        /// </summary>
        public static FrameWcs FromPlateSolve(PlateSolveResult result, double imageWidth, double imageHeight) {
            double degreesPerPixel = result.Pixscale / 3600.0; // arcsec/px -> deg/px (square pixels)

            // Invert NINA's mapping: result.Orientation == wcs.Rotation - 180 (so wcs.Rotation ==
            // Orientation + 180 == 540 - PositionAngle), and result.Flipped == !wcs.Flipped.
            bool isWcsFlipped = !result.Flipped;
            double sign = isWcsFlipped ? 1.0 : -1.0;
            double wcsRotationDegrees = 540.0 - result.PositionAngle; // periodic, so no modulo needed for cos/sin
            double rotationRadians = (isWcsFlipped ? -wcsRotationDegrees : wcsRotationDegrees) * Math.PI / 180.0;
            double cosine = Math.Cos(rotationRadians);
            double sine = Math.Sin(rotationRadians);

            return new FrameWcs(
                CrVal1: result.Coordinates.RADegrees,
                CrVal2: result.Coordinates.Dec,
                CrPix1: imageWidth / 2.0,
                CrPix2: imageHeight / 2.0,
                Cd11: sign * degreesPerPixel * cosine,
                Cd12: sign * degreesPerPixel * sine,
                Cd21: -degreesPerPixel * sine,
                Cd22: degreesPerPixel * cosine);
        }

        public WorldCoordinateSystem ToNinaWcs() => new WorldCoordinateSystem(
            crval1: CrVal1, crval2: CrVal2,
            crpix1: CrPix1, crpix2: CrPix2,
            cd1_1: Cd11, cd1_2: Cd12,
            cd2_1: Cd21, cd2_2: Cd22);
    }
}