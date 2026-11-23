using System;

namespace DiskMap.Core.Remote
{
    /// <summary>
    /// Транспорт запуска агента: получает строки NDJSON по мере поступления.
    /// Реализации: WinRM (PowerShellTransport) и локальный дочерний процесс
    /// (LocalAgentTransport — для отладки без WinRM). Хост-парсер от транспорта
    /// не зависит — цифры одинаковы.
    /// </summary>
    internal interface IAgentTransport
    {
        /// <summary>
        /// Запускает агента для каталога target (путь в системе, где крутится агент)
        /// и вызывает onLine для каждой строки. noMft — принудительный обход.
        /// Возвращает код возврата агента.
        /// </summary>
        int StreamAgent(string target, bool noMft, Action<string> onLine);
    }
}
