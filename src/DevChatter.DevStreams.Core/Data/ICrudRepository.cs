﻿using DevChatter.DevStreams.Core.Model;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DevChatter.DevStreams.Core.Data
{
    public interface ICrudRepository
    {
        Task<int?> Insert<T>(T model) where T : DataEntity;
        Task<T> Get<T>(int id) where T : DataEntity;
        Task<List<T>> GetAll<T>() where T : DataEntity;
        Task<List<T>> GetAll<T>(string filter, object args) where T : DataEntity;
        Task<int> Update<T>(T model) where T : DataEntity;
        Task<int> Delete<T>(int id) where T : DataEntity;
        Task<int> Delete<T>(T model) where T : DataEntity;
        Task<bool> Exists<T>(int id) where T : DataEntity;
    }
}