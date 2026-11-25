using System;

namespace SpaceEngineers.Libs.RadarCore
{
    /// <summary>
    /// Наблюдательное состояние радара.
    /// IDLE — сканирование выключено, объект не выбран.
    /// ACQUIRING — выполняется первичный захват объекта.
    /// LOCKED — объект удерживается в поле зрения, измерения подтверждены.
    /// COASTING — объект утерян, позиция extrapolates по последней скорости.
    /// LOST — объект утерян, предсказание больше не достоверно.
    /// OUT_OF_FOV — объект выходит за доступное поле сканирования камер.
    /// CHARGING — камеры заряжаются, сканирование приостановлено.
    /// </summary>
    public enum RadarState
    {
        IDLE,
        ACQUIRING,
        LOCKED,
        COASTING,
        LOST,
        OUT_OF_FOV,
        CHARGING
    }
}