using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Abstractions;

public interface IFileEventValidator
{
    Result Validate(FileEvent fileEvent);
}
