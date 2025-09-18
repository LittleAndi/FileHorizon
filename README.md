# FileHorizon

**FileHorizon** is an open-source, container-ready file transfer and orchestration system. Designed as a modern alternative to heavyweight integration platforms, it provides a lightweight yet reliable way to move files across **UNC paths, FTP, and SFTP** while ensuring observability and control. By leveraging **Redis** for distributed coordination, FileHorizon can scale out to multiple parallel containers without duplicate processing, making it suitable for both on-premises and hybrid cloud deployments.

Configuration is centralized through **Azure App Configuration** and **Azure Key Vault**, enabling secure, dynamic management of connections and destinations. With **OpenTelemetry** at its core, FileHorizon delivers unified **logging, metrics, and tracing** out of the box—no separate logging stack required. The system emphasizes **safety and consistency**, ensuring files are only picked up once they are fully written at the source.

FileHorizon is built for teams that need the reliability of managed file transfer (MFT) but want the flexibility, transparency, and scalability of modern open-source tooling.
