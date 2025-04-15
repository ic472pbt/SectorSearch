module DFT
    open MathNet.Numerics
    open MathNet.Numerics.IntegralTransforms
    open MathNet.Numerics.LinearAlgebra
    
    /// Converts a byte array to a complex vector, padded to desired length
    let padToLength (bytes: byte[]) (len: int) =
        let arr = Array.zeroCreate len
        Array.blit (bytes |> Array.map float32) 0 arr 0 bytes.Length
        arr |> Array.map (fun a -> Complex32(a, 0.0F))
    
    type DFTSearcher(haystack: byte[], maxNeedleLength) =
        let n = haystack.Length
        let size = Euclid.CeilingToPowerOfTwo(n + maxNeedleLength)
        // Prepare padded haystack for FFT
        let haystackPadded = padToLength haystack size
        // Forward FFT
        do Fourier.Forward(haystackPadded, FourierOptions.AsymmetricScaling)
        // Convert to dense vector
        let vHay = Vector.Build.DenseOfArray(haystackPadded)
        
        /// Computes cross-correlation using FFT
        let crossCorrelate (needle: byte[]) =
            let needleReversed = needle |> Array.rev |> padToLength <| size        
            Fourier.Forward(needleReversed, FourierOptions.AsymmetricScaling)
            let vNee = Vector.Build.DenseOfArray(needleReversed)
    
            // Multiply element-wise (convolution theorem)
            let product = vHay.PointwiseMultiply(vNee).ToArray()
            
            // Inverse FFT to get correlation result
            Fourier.Inverse(product, FourierOptions.AsymmetricScaling)
    
            // Find positions with exact match (correlation == norm)
            let needleNorm =
                needle
                |> Array.map (fun b -> float32 b * float32 b)
                |> Array.sum

            product
            |> Seq.mapi (fun i x -> i, x.Real)
            |> Seq.filter (fun (_, v) -> abs (v - needleNorm) < 1e-3F) // Allow for small float errors
            |> Seq.map (fun (i, _) -> i - needle.Length + 1)
            |> Seq.filter (fun i -> i >= 0 && i + needle.Length <= haystack.Length)
            |> Seq.toList
        
        member _.Search(needle: byte[]) =
            crossCorrelate needle
    
