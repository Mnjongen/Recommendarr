using Recommendarr.Core.Recommendations;

namespace Recommendarr.Core.Tests;

public class VectorMathTests
{
    [Fact]
    public void Cosine_of_identical_vectors_is_one()
    {
        float[] v = [1, 2, 3];
        Assert.Equal(1f, VectorMath.Cosine(v, v), 5);
    }

    [Fact]
    public void Cosine_of_orthogonal_vectors_is_zero()
    {
        Assert.Equal(0f, VectorMath.Cosine([1, 0], [0, 1]), 5);
    }

    [Fact]
    public void Cosine_rejects_dimension_mismatch()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.Cosine([1, 0], [1, 0, 0]));
    }

    [Fact]
    public void Normalize_returns_unit_vector_and_leaves_zero_vector_alone()
    {
        Assert.Equal([0.6f, 0.8f], VectorMath.Normalize([3, 4]));
        Assert.Equal([0f, 0f], VectorMath.Normalize([0, 0]));
    }

    [Fact]
    public void WeightedMean_respects_weights()
    {
        var mean = VectorMath.WeightedMean([[1, 0], [0, 1]], [3, 1]);
        Assert.Equal(0.75f, mean[0], 5);
        Assert.Equal(0.25f, mean[1], 5);
    }
}
