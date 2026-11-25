namespace PseSignalDesk.Application.Services;

/// <summary>
/// Minimal L2-regularized logistic regression (IRLS / Newton-Raphson) used to learn the
/// money-manager factor weights. No third-party numerics package is pulled in: the design
/// matrix is small (twelve standardized factors plus an intercept), so a damped Newton
/// solve with partial pivoting is both sufficient and reviewable.
/// </summary>
internal static class LogisticModelTrainer
{
    /// <summary>Fitted intercept plus one coefficient per standardized feature.</summary>
    public sealed record FitResult(double Intercept, double[] Weights);

    /// <summary>
    /// Fits <c>P(win) = sigmoid(intercept + w · z)</c> with an L2 penalty on the coefficients
    /// (never the intercept). <paramref name="x"/> must already be standardized, so the
    /// penalty is applied on a level playing field across factors.
    /// </summary>
    public static FitResult Fit(double[][] x, double[] y, double lambda, int iterations = 30)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var count = x.Length;
        var features = count == 0 ? 0 : x[0].Length;

        if (count == 0 || features == 0 || y.Length != count)
        {
            return new FitResult(0d, new double[features]);
        }

        var beta = new double[features + 1];

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var gradient = new double[features + 1];
            var hessian = new double[features + 1, features + 1];

            for (var row = 0; row < count; row++)
            {
                var eta = beta[0];
                for (var column = 0; column < features; column++)
                {
                    eta += beta[column + 1] * x[row][column];
                }

                var probability = Sigmoid(eta);
                var weight = Math.Max(probability * (1d - probability), 1e-6d);
                var residual = y[row] - probability;

                gradient[0] += residual;
                hessian[0, 0] += weight;

                for (var column = 0; column < features; column++)
                {
                    gradient[column + 1] += residual * x[row][column];
                    hessian[0, column + 1] += weight * x[row][column];
                    hessian[column + 1, 0] = hessian[0, column + 1];

                    for (var inner = 0; inner < features; inner++)
                    {
                        hessian[column + 1, inner + 1] += weight * x[row][column] * x[row][inner];
                    }
                }
            }

            for (var column = 1; column <= features; column++)
            {
                // The penalty shrinks the coefficients toward zero but leaves the intercept
                // alone, so the base rate is learned rather than assumed.
                gradient[column] -= lambda * beta[column];
                hessian[column, column] += lambda;

                // A hair of ridge keeps the solve stable when a factor is collinear.
                hessian[column, column] += 1e-9d;
            }

            var delta = Solve(hessian, gradient);
            if (delta is null)
            {
                break;
            }

            var maxStep = 0d;
            for (var index = 0; index <= features; index++)
            {
                beta[index] += delta[index];
                maxStep = Math.Max(maxStep, Math.Abs(delta[index]));
            }

            if (maxStep < 1e-9d)
            {
                break;
            }
        }

        return new FitResult(beta[0], beta.Skip(1).ToArray());
    }

    public static double Predict(FitResult model, double[] standardizedFeatures)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(standardizedFeatures);

        var eta = model.Intercept;
        var length = Math.Min(model.Weights.Length, standardizedFeatures.Length);

        for (var index = 0; index < length; index++)
        {
            eta += model.Weights[index] * standardizedFeatures[index];
        }

        return Sigmoid(eta);
    }

    public static double Sigmoid(double eta) =>
        eta >= 0d
            ? 1d / (1d + Math.Exp(-eta))
            : Math.Exp(eta) / (1d + Math.Exp(eta));

    /// <summary>
    /// Gaussian elimination with partial pivoting. Returns null when the system is singular,
    /// which tells the caller to stop iterating instead of trusting a garbage step.
    /// </summary>
    private static double[]? Solve(double[,] matrix, double[] vector)
    {
        var size = vector.Length;
        var augmented = new double[size, size + 1];

        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                augmented[row, column] = matrix[row, column];
            }

            augmented[row, size] = vector[row];
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var best = pivot;

            for (var row = pivot + 1; row < size; row++)
            {
                if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot]))
                {
                    best = row;
                }
            }

            if (Math.Abs(augmented[best, pivot]) < 1e-12d)
            {
                return null;
            }

            if (best != pivot)
            {
                for (var column = pivot; column <= size; column++)
                {
                    var swap = augmented[pivot, column];
                    augmented[pivot, column] = augmented[best, column];
                    augmented[best, column] = swap;
                }
            }

            for (var row = pivot + 1; row < size; row++)
            {
                var factor = augmented[row, pivot] / augmented[pivot, pivot];
                if (factor == 0d)
                {
                    continue;
                }

                for (var column = pivot; column <= size; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        var solution = new double[size];

        for (var row = size - 1; row >= 0; row--)
        {
            var sum = augmented[row, size];

            for (var column = row + 1; column < size; column++)
            {
                sum -= augmented[row, column] * solution[column];
            }

            solution[row] = sum / augmented[row, row];
        }

        return solution;
    }
}
