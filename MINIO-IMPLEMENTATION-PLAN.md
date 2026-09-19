# MinIO Object Storage Implementation Plan (Drive B Blueprint)

> **เอกสารพิมพ์เขียวสำหรับการปรับปรุงระบบจัดเก็บรูปภาพเป็น Object Storage ด้วย MinIO**  
> จัดทำสำหรับสภาพแวดล้อมโปรเจค `B:\Delivery` (Standalone - ตัดการเชื่อมต่อ Git แล้ว)

---

## 1. วัตถุประสงค์ (Objectives)
ย้ายการจัดเก็บรูปภาพอาหาร (`MenuItem.ImageUrl`) จากเดิมที่บันทึกข้อมูล Base64 ขนาดใหญ่ลงในฟิลด์ Database (PostgreSQL) ไปใช้ **MinIO S3-Compatible Object Storage** แทน:
- **ลดขนาดฐานข้อมูล:** ไม่ต้องแบกรับ Large Base64 string ในตาราง PostgreSQL
- **เพิ่มความเร็ว API:** Response payload ของ Menu/Item มีขนาดเล็กลงอย่างมาก (เหลือแค่ URL สตริงสั้นๆ)
- **สเกลได้ง่าย (Scalable):** สามารถต่อ CDN หรือแยก Static Asset Server ได้ในอนาคต

---

## 2. สถาปัตยกรรม MinIO (Architecture)

### 2.1 โครงสร้าง Docker Service (`docker-compose.yml`)
เพิ่ม Service `minio` ลงใน docker-compose ของ Drive B:

```yaml
  minio:
    image: quay.io/minio/minio:latest
    container_name: delivery-minio
    restart: unless-stopped
    ports:
      - "9000:9000"   # S3 API Port
      - "9001:9001"   # MinIO Web Console Port
    environment:
      MINIO_ROOT_USER: minioadmin
      MINIO_ROOT_PASSWORD: miniopassword
    volumes:
      - minio-data:/data
    networks:
      - delivery-network
    command: server /data --console-address ":9001"

volumes:
  minio-data:
    driver: local
```

### 2.2 โครงสร้าง Bucket & Security Policy
- **Bucket Name:** `delivery-media`
- **Access Policy:** `public-read` (หรือ `download`) เพื่อให้ Mobile App / Frontend Browser โหลดภาพตรงได้ผ่าน `http://<host>:9000/delivery-media/...`
- **Folder Convention:**
  - `items/{uuid}.{ext}` สำหรับรูปเมนูอาหาร
  - `riders/{uuid}.{ext}` สำหรับรูปโปรไฟล์/เอกสารไรเดอร์

---

## 3. รายละเอียดการแก้ไขใน BackendApi (.NET 8)

### 3.1 NuGet Packages
ติดตั้ง MinIO Client Library ใน `BackendApi.csproj`:
```bash
dotnet add package Minio
```

### 3.2 Configuration (`appsettings.json`)
```json
"Minio": {
  "Endpoint": "localhost:9000",
  "AccessKey": "minioadmin",
  "SecretKey": "miniopassword",
  "BucketName": "delivery-media",
  "UseSsl": false,
  "PublicUrl": "http://localhost:9000/delivery-media"
}
```

### 3.3 Service Abstraction (`IStorageService`)
สร้าง Interface และ Implementation ใน `BackendApi/Core/Services/`:

```csharp
public interface IStorageService
{
    Task<string> UploadImageAsync(string base64OrStream, string subFolder = "items", CancellationToken ct = default);
    Task<bool> DeleteImageAsync(string fileUrl, CancellationToken ct = default);
}
```

**ขั้นตอนการทำงานของ `UploadImageAsync`:**
1. ตรวจสอบ Magic Bytes (JPEG/PNG/WebP) ก่อน Decode Stream
2. Generate ชื่อไฟล์แบบสุ่ม เช่น `{Guid.NewGuid()}.jpg`
3. สตรีมไฟล์ตรงเข้า MinIO Bucket `delivery-media` ด้วย `PutObjectArgs`
4. คืนค่า Full URL หรือ Relative Path เช่น `http://localhost:9000/delivery-media/items/{guid}.jpg`

### 3.4 Integration ใน Controller / Business Logic
- ใน `StoreController` หรือ `MenuService`:
  - เมื่อรับ `CreateMenuItemDto` หรือ `UpdateMenuItemDto` ที่มีรูปภาพเป็น Base64
  - เรียก `_storageService.UploadImageAsync(dto.ImageUrl)`
  - นำ URL ที่ได้รับไปเซฟลงตาราง `MenuItems.ImageUrl`

---

## 4. สคริปต์ Migration ข้อมูลเดิม (Data Migration Script)
สำหรับรูปภาพเดิมที่ถูกบันทึกเป็น Base64 ในตาราง `MenuItems`:
1. Query record ที่มี `image_url LIKE 'data:image%'`
2. ถอดรหัส Base64 แล้ว PutObject ขึ้น MinIO
3. อัปเดตฟิลด์ `image_url` ในตารางด้วย S3 Object URL ใหม่

---

## 5. แผนการตรวจสอบและทดสอบ (Verification)
1. ตรวจสอบ MinIO Container ทำงานและเข้าหน้า Console `http://localhost:9001` ได้
2. ยิง API เพิ่มเมนูอาหารพร้อม Base64 Data URL → ตรวจสอบว่าใน PostgreSQL บันทึกเป็น URL และใน MinIO Console มีไฟล์ภาพปรากฏ
3. เปิด URL ภาพผ่าน Web Browser ได้ภาพที่แสดงผลถูกต้องสมบูรณ์
