# ChatRoomServer - 聊天室后端服务

基于 ASP.NET Core 8 的聊天室后端，提供 REST API、SignalR 实时通信、MySQL 数据存储、文件上传、用户管理等功能。

## 技术栈
- .NET 8
- ASP.NET Core SignalR
- Entity Framework Core (Pomelo MySQL)
- MySQL 8.0
- JWT 认证
- BCrypt 密码哈希

## 功能特性
- 用户注册/登录/找回密码（SHA-256 + BCrypt）
- 实时聊天（文字、图片、文件）
- 全局背景音乐同步（播放列表、循环模式）
- 在线用户管理（踢出、禁言、封禁、IP 封禁）
- 系统公告（管理员自定义）
- 头像上传与实时更新
- 系统消息持久化（加入/离开/管理操作）
- 文件上传（最大 2GB）
- 数据库自动建表，配置文件自动生成

## 环境要求
- .NET 8 SDK / Runtime
- MySQL 5.7+ (推荐 8.0)
- Windows / Linux

## 快速开始

### 1. 克隆仓库
```bash
git clone https://github.com/RCMOND/ChatRoomServer.git
cd ChatRoomServer
