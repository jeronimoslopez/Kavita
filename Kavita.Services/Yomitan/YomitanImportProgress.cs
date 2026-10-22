using System;
using System.Collections.Generic;
using Kavita.Models.DTOs.Yomitan;

namespace Kavita.Services.Yomitan;

/// <summary>
/// Thread-safe progress of the bundled dictionary import, read by the status endpoint while the import runs
/// </summary>
public class YomitanImportProgress
{
    private readonly object _lock = new();
    private YomitanImportState _state = YomitanImportState.Idle;
    private string? _currentDictionary;
    private string? _currentTitle;
    private int _banksDone;
    private int _banksTotal;
    private int _dictionariesDone;
    private int _dictionariesTotal;
    private readonly List<string> _errors = [];
    private DateTime? _startedUtc;
    private DateTime? _completedUtc;

    public void Start(int dictionariesTotal)
    {
        lock (_lock)
        {
            _state = YomitanImportState.Importing;
            _dictionariesTotal = dictionariesTotal;
            _dictionariesDone = 0;
            _banksDone = 0;
            _banksTotal = 0;
            _currentDictionary = null;
            _currentTitle = null;
            _errors.Clear();
            _startedUtc = DateTime.UtcNow;
            _completedUtc = null;
        }
    }

    public void BeginDictionary(string fileName)
    {
        lock (_lock)
        {
            _currentDictionary = fileName;
            _currentTitle = null;
            _banksDone = 0;
            _banksTotal = 0;
        }
    }

    public void SetBanks(string title, int banksTotal)
    {
        lock (_lock)
        {
            _currentTitle = title;
            _banksTotal = banksTotal;
            _banksDone = 0;
        }
    }

    public void BankDone()
    {
        lock (_lock)
        {
            _banksDone++;
        }
    }

    public void EndDictionary(string? error = null)
    {
        lock (_lock)
        {
            _dictionariesDone++;
            if (error != null) _errors.Add(error);
        }
    }

    public void Finish(string? error = null)
    {
        lock (_lock)
        {
            if (error != null) _errors.Add(error);
            _state = _errors.Count > 0 ? YomitanImportState.Failed : YomitanImportState.Completed;
            _currentDictionary = null;
            _currentTitle = null;
            _completedUtc = DateTime.UtcNow;
        }
    }

    public YomitanImportStatusDto Snapshot()
    {
        lock (_lock)
        {
            return new YomitanImportStatusDto
            {
                State = _state,
                CurrentDictionary = _currentDictionary,
                CurrentTitle = _currentTitle,
                BanksDone = _banksDone,
                BanksTotal = _banksTotal,
                DictionariesDone = _dictionariesDone,
                DictionariesTotal = _dictionariesTotal,
                Errors = [.._errors],
                StartedUtc = _startedUtc,
                CompletedUtc = _completedUtc,
            };
        }
    }
}
