using Hexa.Models;

namespace Hexa.Services;

/// <summary>
/// Stateful six-axis motion envelope. Every normal motion source passes through this
/// limiter so imported scripts and direct controls cannot bypass the hardware limits.
/// </summary>
public sealed class MotionSafetyLimiter
{
    private static readonly double[] AxisSpeedScale = [1.0, 0.78, 0.78, 0.82, 0.68, 0.68];


    private readonly double[] _position = [50, 50, 50, 50, 50, 50];
    private readonly double[] _velocity = new double[6];
    private readonly double[] _acceleration = new double[6];

    public double[] Position => _position.ToArray();
    public double[] Velocity => _velocity.ToArray();
    public double[] Acceleration => _acceleration.ToArray();

    public void Reset(IReadOnlyList<double> position)
    {
        for (int i = 0; i < 6; i++)
            _position[i] = i < position.Count && double.IsFinite(position[i])
                ? Math.Clamp(position[i], 0, 100)
                : 50;
        Array.Clear(_velocity);
        Array.Clear(_acceleration);
    }

    public double[] Step(IReadOnlyList<double> target, double deltaSeconds, ComfortProfile profile)
    {
        double dt = Math.Clamp(double.IsFinite(deltaSeconds) ? deltaSeconds : 0.02, 0.005, 0.1);
        var output = new double[6];

        for (int i = 0; i < output.Length; i++)
        {
            double desiredPosition = i < target.Count && double.IsFinite(target[i])
                ? Math.Clamp(target[i], 0, 100)
                : _position[i];
            double speedLimit = profile.MaxAxisSpeedPerSecond * AxisSpeedScale[i];
            double accelerationLimit = profile.MaxAxisAccelerationPerSecond2 * AxisSpeedScale[i];
            double jerkLimit = profile.MaxAxisJerkPerSecond3 * AxisSpeedScale[i];

            double remaining = desiredPosition - _position[i];
            double direction = Math.Sign(remaining);
            double distance = Math.Abs(remaining);
            double towardVelocity = _velocity[i] * direction;
            double towardAcceleration = _acceleration[i] * direction;
            double stopDistance = towardVelocity > 0
                ? StoppingDistance(towardVelocity, towardAcceleration, accelerationLimit, jerkLimit)
                : 0;
            double discreteMargin = Math.Max(0, towardVelocity * dt + 0.5 * towardAcceleration * dt * dt);
            // 命令速度必须落在「用剩下的距离还刹得住」的范围内。
            //
            // 原来这里是「distance > stopDistance 就命令全速」，问题出在朝着目标全速冲的那一段：
            // 命令速度是限速上限（沉浸档 360/秒），但剩下 10 个点时从 360/秒已经刹不住了
            // （保持加速度上限也要 27 个点），于是冲过目标再往回追，追回来又冲过头 —— 自激振荡。
            // 实测：目标恒定不动时输出仍在 ±6 个行程点上来回；20 个点的阶跃冲过头 11.5 个点、
            // 要 0.9 秒才停。脚本播放里就是「快段落结束、脚本停住不动了，设备还在那儿荡」。
            // 现在把命令速度按同一套物理模型反解成「刹得住的速度」：远处照样是限速（不影响响应速度），
            // 近处自动进入减速曲线，落点带零速度 —— 不再有来回。
            double desiredVelocity = distance <= stopDistance + discreteMargin
                ? 0
                : direction * SpeedThatStopsWithin(distance, towardAcceleration, accelerationLimit, jerkLimit, speedLimit);
            double desiredAcceleration = Math.Clamp(
                (desiredVelocity - _velocity[i]) / dt,
                -accelerationLimit,
                accelerationLimit);
            double accelerationDelta = Math.Clamp(
                desiredAcceleration - _acceleration[i],
                -jerkLimit * dt,
                jerkLimit * dt);

            double nextAcceleration = Math.Clamp(
                _acceleration[i] + accelerationDelta,
                -accelerationLimit,
                accelerationLimit);
            double nextVelocity = Math.Clamp(
                _velocity[i] + nextAcceleration * dt,
                -speedLimit,
                speedLimit);
            double movement = nextVelocity * dt;

            bool wouldCrossTarget = Math.Abs(movement) >= Math.Abs(remaining)
                || Math.Sign(movement) != Math.Sign(remaining);
            double trackingVelocity = remaining / dt;
            double trackingAcceleration = (trackingVelocity - _velocity[i]) / dt;
            double trackingJerk = (trackingAcceleration - _acceleration[i]) / dt;
            bool canTrackExactly = wouldCrossTarget
                && Math.Abs(trackingVelocity) <= speedLimit
                && Math.Abs(trackingAcceleration) <= accelerationLimit
                && Math.Abs(trackingJerk) <= jerkLimit;

            if (canTrackExactly)
            {
                _position[i] = desiredPosition;
                _velocity[i] = trackingVelocity;
                _acceleration[i] = trackingAcceleration;
            }
            else
            {
                _position[i] = Math.Clamp(_position[i] + movement, 0, 100);
                _velocity[i] = nextVelocity;
                _acceleration[i] = nextAcceleration;
            }

            output[i] = _position[i];
        }

        return output;
    }

    /// <summary>
    /// 二分反解：<b>从当前速度刹停</b>所需路程不超过 <paramref name="distance"/> 的最大速度。
    /// 用的是和刹车判据完全相同的 <see cref="StoppingDistance"/>，所以两者永远自洽。
    /// </summary>
    private static double SpeedThatStopsWithin(
        double distance, double acceleration, double accelerationLimit, double jerkLimit, double speedLimit)
    {
        double low = 0, high = speedLimit;
        for (int i = 0; i < 10; i++)
        {
            double mid = (low + high) / 2;
            if (StoppingDistance(mid, acceleration, accelerationLimit, jerkLimit) <= distance) low = mid;
            else high = mid;
        }
        return low;
    }

    private static double StoppingDistance(double velocity, double acceleration, double maxAcceleration, double jerk)
    {
        double v = Math.Max(0, velocity);
        double a = Math.Clamp(acceleration, -maxAcceleration, maxAcceleration);
        double timeToZeroDuringJerk = (a + Math.Sqrt(Math.Max(0, a * a + 2 * jerk * v))) / jerk;
        double timeToNegativeLimit = (a + maxAcceleration) / jerk;
        if (timeToZeroDuringJerk <= timeToNegativeLimit)
            return Math.Max(0, v * timeToZeroDuringJerk
                + 0.5 * a * timeToZeroDuringJerk * timeToZeroDuringJerk
                - jerk * timeToZeroDuringJerk * timeToZeroDuringJerk * timeToZeroDuringJerk / 6);

        double t = Math.Max(0, timeToNegativeLimit);
        double distanceDuringJerk = v * t + 0.5 * a * t * t - jerk * t * t * t / 6;
        double velocityAfterJerk = Math.Max(0, v + a * t - 0.5 * jerk * t * t);
        return Math.Max(0, distanceDuringJerk + velocityAfterJerk * velocityAfterJerk / (2 * maxAcceleration));
    }
}
